using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Review;

/// <summary>A change made to the cues: what to call it, and which cue to select afterwards (-1 for none).</summary>
/// <param name="Undone">the change was an undo or redo</param>
public sealed record CueEdit(string Name, int Select, bool Undone = false);

/// <summary>
/// The subtitles of one file while they are reviewed: the edits the review screen makes (text, timing, split, merge,
/// delete, add, replace a stretch with a re-run), each one a step that can be undone and redone, plus the lookups the
/// screen needs (the cue at a time, search, flags). Cues stay in time order and edits never make them overlap.
/// </summary>
public sealed class CueEditor
{
    /// <summary>The shortest a cue can be made by editing.</summary>
    public const double MinCueSeconds = 0.2;

    /// <summary>How long a new cue is, when there is room.</summary>
    public const double NewCueSeconds = 2.0;

    private const int MaxUndo = 500;

    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];
    private readonly HashSet<(FlagKind, long)> _dismissed = [];
    private Cue[] _cues;
    private Cue[] _saved;
    private double _duration;
    private List<CueFlag>? _flags;

    /// <summary>The cues before a change, and the change's name and selection.</summary>
    private sealed record Step(Cue[] Cues, string Name, int Select);

    /// <param name="duration">the file's length in seconds (0 if not known yet)</param>
    /// <param name="unsure">look-alike words Whisper was not sure of, flagged while a cue still says them</param>
    public CueEditor(IEnumerable<Cue> cues, double duration, IReadOnlyList<UnsureWord>? unsure = null)
    {
        _cues = [.. cues.OrderBy(c => c.Start)];
        _saved = _cues;
        _duration = duration;
        Unsure = unsure ?? [];
    }

    public IReadOnlyList<UnsureWord> Unsure { get; }

    public event EventHandler<CueEdit>? Changed;

    public IReadOnlyList<Cue> Cues => _cues;

    /// <summary>The file's length; at least the end of the last cue.</summary>
    public double Duration
    {
        get => Math.Max(_duration, _cues.Length > 0 ? _cues[^1].End : 0);
        set
        {
            _duration = value;
            _flags = null;
        }
    }

    /// <summary>Changed since <see cref="MarkSaved"/> (or since loading).</summary>
    public bool IsDirty => !_cues.AsSpan().SequenceEqual(_saved);

    public void MarkSaved() => _saved = _cues;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    /// <summary>Things to check (<see cref="CueFlags"/>), without the ones dismissed.</summary>
    public IReadOnlyList<CueFlag> Flags =>
        _flags ??= [.. CueFlags.Find(_cues, Duration, Unsure).Where(f => !_dismissed.Contains(Key(f)))];

    /// <summary>Hide a flag the user has checked; it stays hidden while the same thing is at the same time.</summary>
    public void Dismiss(CueFlag flag)
    {
        _dismissed.Add(Key(flag));
        _flags = null;
        Changed?.Invoke(this, new CueEdit("Dismiss", flag.Cue ?? -1));
    }

    /// <summary>The cue showing at <paramref name="time"/>, or -1.</summary>
    public int IndexAt(double time)
    {
        var i = LastStartingBy(time);
        return i >= 0 && time < _cues[i].End ? i : -1;
    }

    /// <summary>The cue showing at <paramref name="time"/>, or else the next one; -1 after the last cue.</summary>
    public int IndexFrom(double time)
    {
        var i = LastStartingBy(time);
        if (i >= 0 && time < _cues[i].End)
            return i;
        return i + 1 < _cues.Length ? i + 1 : -1;
    }

    /// <summary>The cues whose text contains <paramref name="query"/>, ignoring case; all cues for an empty query.</summary>
    public List<int> Search(string query)
    {
        query = query.Trim();
        return [.. Enumerable.Range(0, _cues.Length)
                             .Where(i => query.Length == 0 || _cues[i].Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))];
    }

    /// <summary>How far cue <paramref name="index"/> may reach without overlapping its neighbours.</summary>
    public (double Start, double End) Limits(int index)
    {
        var cue = _cues[index];
        var start = index > 0 ? Math.Min(_cues[index - 1].End, cue.Start) : 0;
        var end = index + 1 < _cues.Length ? Math.Max(_cues[index + 1].Start, cue.End) : Math.Max(Duration, cue.End);
        return (start, end);
    }

    /// <summary>
    /// The times cue <paramref name="index"/> would get for <paramref name="start"/> and <paramref name="end"/>: kept
    /// between its neighbours, at least <see cref="MinCueSeconds"/> long. What dragging an edge shows before letting go.
    /// </summary>
    public (double Start, double End) ClampTimes(int index, double start, double end)
    {
        var (lo, hi) = Limits(index);
        start = Math.Clamp(start, lo, Math.Max(lo, hi - MinCueSeconds));
        end = Math.Clamp(end, Math.Min(start + MinCueSeconds, hi), hi);
        return (Math.Round(start, 3), Math.Round(end, 3));
    }

    /// <summary>Change a cue's text; empty text deletes the cue.</summary>
    /// <returns>false when nothing changed</returns>
    public bool SetText(int index, string text)
    {
        text = Py.Strip(Py.CollapseWhitespace(text));
        if (text.Length == 0)
            return Delete(index);
        if (text == _cues[index].Text)
            return false;
        return Apply(Replaced(index, 1, _cues[index] with { Text = text }), "Edit text", index);
    }

    /// <summary>Move a cue's start and end, kept within <see cref="ClampTimes"/>.</summary>
    public bool SetTimes(int index, double start, double end)
    {
        var (s, e) = ClampTimes(index, start, end);
        var cue = _cues[index];
        if (s == cue.Start && e == cue.End)
            return false;
        return Apply(Replaced(index, 1, cue with { Start = s, End = e }), "Change timing", index);
    }

    /// <summary>
    /// Split a cue in two between words: at the space nearest <paramref name="textPosition"/> (the middle when it is at
    /// either end). The new boundary is at <paramref name="at"/> when that is inside the cue (the playhead), else where
    /// the text divides.
    /// </summary>
    /// <returns>the second part's index, or -1 when the cue is one word or too short to split</returns>
    public int Split(int index, int textPosition, double? at = null)
    {
        var cue = _cues[index];
        var text = cue.Text;
        var spaces = Enumerable.Range(0, text.Length).Where(i => text[i] == ' ').ToList();
        if (spaces.Count == 0 || cue.End - cue.Start < 2 * MinCueSeconds)
            return -1;
        var target = textPosition <= 0 || textPosition >= text.Length ? text.Length / 2.0 : textPosition;
        var space = spaces.MinBy(s => Math.Abs(s + 0.5 - target));
        var (left, right) = (text[..space].Trim(), text[(space + 1)..].Trim());
        var middle = at is { } t && t > cue.Start + MinCueSeconds && t < cue.End - MinCueSeconds
            ? t
            : cue.Start + (cue.End - cue.Start) * left.Length / (left.Length + right.Length);
        middle = Math.Round(Math.Clamp(middle, cue.Start + MinCueSeconds, cue.End - MinCueSeconds), 3);
        Apply(Replaced(index, 1, new Cue(cue.Start, middle, left), new Cue(middle, cue.End, right)), "Split", index + 1);
        return index + 1;
    }

    /// <summary>Join a cue with the one after it.</summary>
    public bool Merge(int index)
    {
        if (index < 0 || index + 1 >= _cues.Length)
            return false;
        var (a, b) = (_cues[index], _cues[index + 1]);
        return Apply(Replaced(index, 2, new Cue(a.Start, Math.Max(a.End, b.End), $"{a.Text} {b.Text}")), "Merge", index);
    }

    public bool Delete(int first, int count = 1)
    {
        if (first < 0 || count <= 0 || first + count > _cues.Length)
            return false;
        var select = Math.Min(first, _cues.Length - count - 1);
        return Apply(Replaced(first, count), count == 1 ? "Delete" : $"Delete {count} subtitles", select);
    }

    /// <summary>
    /// Add a cue at <paramref name="at"/> (after the cue showing there, if any), <see cref="NewCueSeconds"/> long or
    /// up to the next cue.
    /// </summary>
    /// <returns>its index, or -1 when there is no room</returns>
    public int Insert(double at, string text)
    {
        var i = LastStartingBy(at) + 1;
        var start = Math.Max(0, i > 0 ? Math.Max(at, _cues[i - 1].End) : at);
        var limit = i < _cues.Length ? _cues[i].Start : Math.Max(Duration, start + NewCueSeconds);
        var end = Math.Min(start + NewCueSeconds, limit);
        if (end - start < MinCueSeconds)
            return -1;
        Apply(Replaced(i, 0, new Cue(Math.Round(start, 3), Math.Round(end, 3), Py.Strip(text))), "Add subtitle", i);
        return i;
    }

    /// <summary>
    /// Replace the cues starting in [<paramref name="start"/>, <paramref name="end"/>) with <paramref name="cues"/> (a
    /// re-run of that stretch), kept between the cues around it.
    /// </summary>
    /// <returns>the index of the first new cue (or where they would have gone)</returns>
    public int ReplaceRange(double start, double end, IReadOnlyList<Cue> cues, string name)
    {
        var first = Array.FindIndex(_cues, c => c.Start >= start);
        if (first < 0)
            first = _cues.Length;
        var count = _cues.Skip(first).TakeWhile(c => c.Start < end).Count();
        var lo = first > 0 ? _cues[first - 1].End : 0;
        var hi = first + count < _cues.Length ? _cues[first + count].Start : double.MaxValue;
        var added = new List<Cue>();
        foreach (var cue in cues.OrderBy(c => c.Start))
        {
            var s = Math.Max(cue.Start, added.Count > 0 ? added[^1].End : lo);
            var e = Math.Min(cue.End, hi);
            if (e - s >= MinCueSeconds && Py.Strip(cue.Text).Length > 0)
                added.Add(new Cue(Math.Round(s, 3), Math.Round(e, 3), cue.Text));
        }
        Apply(Replaced(first, count, [.. added]), name, added.Count > 0 ? first : Math.Min(first, _cues.Length - count - 1));
        return first;
    }

    public bool Undo() => Move(_undo, _redo);

    public bool Redo() => Move(_redo, _undo);

    private bool Move(List<Step> from, List<Step> to)
    {
        if (from.Count == 0)
            return false;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(step with { Cues = _cues });
        Set(step.Cues, new CueEdit(step.Name, Math.Min(step.Select, step.Cues.Length - 1), Undone: true));
        return true;
    }

    private bool Apply(Cue[] cues, string name, int select)
    {
        _undo.Add(new Step(_cues, name, select));
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _redo.Clear();
        Set(cues, new CueEdit(name, Math.Min(select, cues.Length - 1)));
        return true;
    }

    private void Set(Cue[] cues, CueEdit edit)
    {
        _cues = cues;
        _flags = null;
        Changed?.Invoke(this, edit);
    }

    /// <summary>The cues with <paramref name="count"/> of them from <paramref name="index"/> replaced by <paramref name="with"/>.</summary>
    private Cue[] Replaced(int index, int count, params Cue[] with) => [.. _cues[..index], .. with, .. _cues[(index + count)..]];

    /// <summary>The last cue starting at or before <paramref name="time"/>, or -1.</summary>
    private int LastStartingBy(double time)
    {
        int lo = 0, hi = _cues.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_cues[mid].Start <= time)
                (found, lo) = (mid, mid + 1);
            else
                hi = mid - 1;
        }
        return found;
    }

    private static (FlagKind, long) Key(CueFlag flag) => (flag.Kind, (long)Math.Round(flag.Start * 1000));
}
