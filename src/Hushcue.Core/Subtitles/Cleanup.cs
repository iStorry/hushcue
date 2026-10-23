using System.Text.RegularExpressions;

namespace Hushcue.Core.Subtitles;

/// <summary>
/// Turns raw Whisper words into readable subtitles: drops non-speech markers and hallucinations, caps repetition
/// loops, groups words into cues and fixes their timing. The golden tests pin its output byte for byte.
/// </summary>
public static partial class Cleanup
{
    // Netflix's English (USA) Timed Text Style Guide: two lines of 42 characters, 5/6 s to 7 s on screen, up to 20
    // characters per second. The 2-frame gap is from its supplementals guide, here at 24 fps (the longest common gap)
    public const int MaxLine = 42;
    public const int MaxCueChars = 84;
    public const double MaxCueSeconds = 7.0;
    public const double MinCueSeconds = 5.0 / 6.0;
    public const double MaxCharsPerSecond = 20;
    public const double CueGapSeconds = 2.0 / 24.0;
    public const double WordGapSplit = 0.8;
    public const double MinCueGap = 0.2; // cues starting closer than this cannot both be shown; they are merged

    // Weighing breaks (Breaks.Cost is 0..9): a break worth taking costs less than a few characters of unevenness
    private const double BreakWeight = 4;
    private const double OverflowPenalty = 1000;
    private const double ShortTopLinePenalty = 12;
    private const double LookbackWeight = 0.25;
    private const int MinSplitChars = 20;
    private const double ShortSplitPenalty = 30;

    /// <summary>Whisper habitually emits these when it "hears" silence or noise; people also say them, so they are
    /// dropped only when little speech is heard under them (Pipeline.UnheardCues) and flagged in the review.</summary>
    public static readonly IReadOnlySet<string> Hallucinations = new HashSet<string>
    {
        "thank you for watching", "thanks for watching", "thank you so much for watching",
        "thank you for listening", "thanks for listening", "thank you for listening to the show",
        "thanks for listening to the show",
        "please subscribe", "like and subscribe", "subscribe to my channel",
        "subtitles by the amaraorg community", "subtitles by the amara org community",
        "transcribed by",
    };

    // Repeats needed before a loop is capped, by phrase length. One word looping ('mmm' x16) is a clear failure
    // from 5 repeats; a two/three-word phrase ('oh, wow, oh, wow, ...') from 4.
    private static readonly int[] MinRepeats = [0, 5, 4, 4];

    /// <summary>Everything after decoding: loops capped, words grouped into cues, junk dropped, timing fixed.</summary>
    public static List<Cue> ToCues(IReadOnlyList<Word> words) =>
        FixTiming(DropJunk(MergeOrphans(BuildCues(CollapseRepeats(words)))));

    /// <summary>
    /// Drop non-speech markers such as (laughs), [Music], *sighs*, ♪ lyrics ♪. Works on words so a marker split over
    /// several words is removed whole. A '*' inside a word (5*3) is not treated as a marker.
    /// </summary>
    public static List<Word> StripMarkers(IEnumerable<Word> words)
    {
        var output = new List<Word>();
        var depth = 0;
        var toggled = false;
        foreach (var w in words)
        {
            var t = Py.Strip(w.Text);
            if (t.Length == 0)
                continue;
            var opens = Count(t, '(') + Count(t, '[');
            var closes = Count(t, ')') + Count(t, ']');
            var startsOpen = t[0] is '*' or '♪';
            var endsClose = Py.Len(t) > 1 && t[^1] is '*' or '♪';
            var inside = depth > 0 || opens > 0;
            if (toggled)
            {
                inside = true;
                if (endsClose || t is "*" or "♪")
                    toggled = false;
            }
            else if (startsOpen)
            {
                inside = true;
                if (!endsClose && (Py.Len(t) > 1 || t == "♪"))
                    toggled = true;
            }
            depth = Math.Max(0, depth + opens - closes);
            if (!inside && closes == 0)
                output.Add(w);
        }
        return output;
    }

    /// <summary>
    /// Shorten stretched letters inside one token ('Mmmmm...' x447 -> 'Mmmm', 'Ahhhhh' -> 'Ahhh'). Whisper sometimes
    /// emits one enormous 'Mmmm...' token for a hum, which CollapseRepeats cannot see. Only letters are touched.
    /// </summary>
    public static List<Word> SquashStretched(IEnumerable<Word> words) =>
        [.. words.Select(w => w with { Text = Stretch().Replace(w.Text, "$1$1$1") })];

    /// <summary>
    /// Locate loops: a 1-3 word phrase repeated past its threshold.
    /// Returns non-overlapping (start index, words covered, phrase length) in order.
    /// </summary>
    public static List<(int Start, int Covered, int PhraseLength)> FindRepeatRuns(IReadOnlyList<Word> words)
    {
        var keys = words.Select(WordKey).ToArray();
        var runs = new List<(int, int, int)>();
        var i = 0;
        while (i < keys.Length)
        {
            (int Covered, int K)? best = null;
            for (var k = 1; k <= 3; k++)
            {
                if (i + k > keys.Length || !keys.AsSpan(i, k).ToArray().All(key => key.Length > 0))
                    continue;
                var reps = 1;
                while (i + (reps + 1) * k <= keys.Length && keys.AsSpan(i + reps * k, k).SequenceEqual(keys.AsSpan(i, k)))
                    reps++;
                if (reps >= MinRepeats[k] && (best is null || reps * k > best.Value.Covered))
                    best = (reps * k, k);
            }
            if (best is null)
            {
                i++;
            }
            else
            {
                runs.Add((i, best.Value.Covered, best.Value.K));
                i += best.Value.Covered;
            }
        }
        return runs;
    }

    /// <summary>
    /// Cap runaway repetition of a word or short phrase, a typical Whisper loop. A loop keeps its first
    /// <paramref name="keep"/> repeats; the last kept word's end is stretched over the run (at most
    /// <paramref name="maxExtend"/> seconds) so the subtitle still covers the sound instead of vanishing early.
    /// </summary>
    public static List<Word> CollapseRepeats(IReadOnlyList<Word> words, int keep = 3, double maxExtend = 2.0)
    {
        var output = new List<Word>();
        var pos = 0;
        foreach (var (i, covered, k) in FindRepeatRuns(words))
        {
            output.AddRange(words.Skip(pos).Take(i - pos));
            var kept = words.Skip(i).Take(keep * k).ToList();
            var last = kept[^1];
            kept[^1] = last with { End = Math.Max(last.End, Math.Min(words[i + covered - 1].End, last.Start + maxExtend)) };
            output.AddRange(kept);
            pos = i + covered;
        }
        output.AddRange(words.Skip(pos));
        return output;
    }

    /// <summary>
    /// Split a cue longer than <see cref="MaxLine"/> into two lines, both within it when that is possible: at the
    /// best place by <see cref="Breaks"/>, preferring lines of similar length, a bottom line longer than the top
    /// (Netflix's "bottom-heavy pyramid") and more than two words on top.
    /// </summary>
    public static string WrapLines(string text)
    {
        text = Py.Strip(Py.CollapseWhitespace(text));
        if (Py.Len(text) <= MaxLine)
            return text;
        var words = text.Split(' ');
        var best = 1;
        var bestCost = double.PositiveInfinity;
        for (var i = 1; i < words.Length; i++)
        {
            var top = Py.Len(string.Join(' ', words[..i]));
            var bottom = Py.Len(string.Join(' ', words[i..]));
            var cost = LineBreakCost(words[i - 1], words[i], top, bottom, i);
            if (cost < bestCost)
                (best, bestCost) = (i, cost);
        }
        return string.Join(' ', words[..best]) + "\n" + string.Join(' ', words[best..]);
    }

    private static double LineBreakCost(string before, string after, int top, int bottom, int topWords)
    {
        var overflow = Math.Max(0, top - MaxLine) + Math.Max(0, bottom - MaxLine);
        var imbalance = top > bottom ? top - bottom : (bottom - top) / 2.0;
        var shortTop = topWords <= 2 ? ShortTopLinePenalty : 0;
        return overflow * OverflowPenalty + Breaks.Cost(before, after) * BreakWeight + imbalance + shortTop;
    }

    /// <summary>
    /// Group words into cues: a new cue after a pause, after a sentence once the cue is long enough to read, and
    /// before the cue would pass <see cref="MaxCueChars"/> or <see cref="MaxCueSeconds"/>. A cue that is full is cut
    /// at its best break (<see cref="BestLimitSplit"/>), not simply before the word that did not fit.
    /// </summary>
    public static List<Cue> BuildCues(IEnumerable<Word> words)
    {
        var cues = new List<Cue>();
        var cur = new List<Word>();

        void Flush(int count)
        {
            var text = Py.Strip(Py.CollapseWhitespace(Joined(cur.Take(count))));
            if (text.Length > 0)
                cues.Add(new Cue(cur[0].Start, cur[count - 1].End, text));
            cur.RemoveRange(0, count);
        }

        foreach (var w in words)
        {
            if (cur.Count > 0)
            {
                var chars = Py.Len(Py.Strip(Joined(cur)));
                if (w.Start - cur[^1].End > WordGapSplit)
                    Flush(cur.Count);
                else if (w.End - cur[0].Start > MaxCueSeconds || chars + Py.Len(Py.Strip(w.Text)) + 1 > MaxCueChars)
                    Flush(BestLimitSplit(cur, w));
                else if (EndsSentence(cur[^1].Text) && (chars >= 30 || cur[^1].End - cur[0].Start >= 2.5))
                    Flush(cur.Count);
            }
            cur.Add(w);
        }
        if (cur.Count > 0)
            Flush(cur.Count);
        return cues;
    }

    /// <summary>
    /// How many words of the full cue <paramref name="cur"/> to keep when <paramref name="next"/> does not fit: the
    /// best break by <see cref="Breaks"/>, a little dearer the further back it is, never leaving a first part shorter
    /// than <see cref="MinSplitChars"/> when a longer one will do, and never leaving the rest (with
    /// <paramref name="next"/>) too long to show.
    /// </summary>
    private static int BestLimitSplit(List<Word> cur, Word next)
    {
        var total = Py.Len(Py.Strip(Joined(cur)));
        var best = cur.Count;
        var bestCost = double.PositiveInfinity;
        for (var k = 1; k <= cur.Count; k++)
        {
            var kept = Py.Len(Py.Strip(Joined(cur.Take(k))));
            var rest = Py.Len(Py.Strip(Joined(cur.Skip(k).Append(next))));
            if (k < cur.Count && (next.End - cur[k].Start > MaxCueSeconds || rest > MaxCueChars))
                continue;
            var after = k < cur.Count ? cur[k] : next;
            var cost = Breaks.Cost(cur[k - 1].Text, after.Text) * BreakWeight
                       + (total - kept) * LookbackWeight
                       + (kept < MinSplitChars ? ShortSplitPenalty : 0);
            if (cost < bestCost)
                (best, bestCost) = (k, cost);
        }
        return best;
    }

    /// <summary>Join tiny unfinished fragments ('Oh,' + 'my God.') into the next cue.</summary>
    public static List<Cue> MergeOrphans(IReadOnlyList<Cue> cues)
    {
        var output = new List<Cue>();
        var i = 0;
        while (i < cues.Count)
        {
            var c = cues[i];
            if (i + 1 < cues.Count)
            {
                var n = cues[i + 1];
                var few = Py.Split(c.Text).Length <= 2;
                var unfinished = !EndsSentence(c.Text);
                if (few && unfinished && n.Start - c.End <= 1.5
                    && n.End - c.Start <= MaxCueSeconds + 0.5
                    && Py.Len(c.Text) + Py.Len(n.Text) + 1 <= MaxCueChars)
                {
                    output.Add(new Cue(c.Start, n.End, c.Text + " " + n.Text));
                    i += 2;
                    continue;
                }
            }
            output.Add(c);
            i++;
        }
        return output;
    }

    /// <summary>Remove empty cues and runaway repetition (at most two identical cues in a row). Known hallucination
    /// phrases are checked later, against the audio (Pipeline.UnheardCues).</summary>
    public static List<Cue> DropJunk(IEnumerable<Cue> cues)
    {
        var output = new List<Cue>();
        string? runText = null;
        var runLength = 0;
        foreach (var c in cues)
        {
            var n = Normalize(c.Text);
            if (n.Length == 0)
                continue;
            if (n == runText)
            {
                runLength++;
            }
            else
            {
                runText = n;
                runLength = 1;
            }
            if (runLength >= 3)
                continue;
            output.Add(c);
        }
        return output;
    }

    /// <summary>
    /// Merge cues that start together; keep every cue on screen long enough to read (at least
    /// <see cref="MinCueSeconds"/>, and its text at <see cref="MaxCharsPerSecond"/>, up to <see cref="MaxCueSeconds"/>)
    /// by holding it into the silence after it; leave <see cref="CueGapSeconds"/> before the next cue; never overlap.
    /// </summary>
    public static List<Cue> FixTiming(IEnumerable<Cue> cues)
    {
        var merged = new List<Cue>();
        foreach (var c in cues.OrderBy(c => c.Start))
        {
            if (merged.Count > 0 && c.Start - merged[^1].Start < MinCueGap)
                merged[^1] = new Cue(merged[^1].Start, Math.Max(merged[^1].End, c.End), $"{merged[^1].Text} {c.Text}");
            else
                merged.Add(c);
        }
        for (var i = 0; i < merged.Count; i++)
        {
            var c = merged[i];
            var readable = Math.Min(Math.Max(MinCueSeconds, Py.Len(c.Text) / MaxCharsPerSecond), MaxCueSeconds);
            var end = Math.Max(c.End, c.Start + readable);
            if (i + 1 < merged.Count && end > merged[i + 1].Start - CueGapSeconds)
                end = Math.Max(c.Start + 0.2, merged[i + 1].Start - CueGapSeconds);
            merged[i] = c with { End = end };
        }
        return merged;
    }

    /// <summary>Flag results that look empty or cut short instead of reporting plain success.</summary>
    public static List<string> CoverageWarnings(IReadOnlyList<Cue> cues, double duration)
    {
        if (cues.Count == 0)
            return ["No speech was found. Try the Thorough preset, or check the file has audible dialogue."];
        var output = new List<string>();
        if (cues[^1].End < duration * 0.25)
        {
            output.Add($"Subtitles stop at {Py.F0(cues[^1].End / 60)} min of a {Py.F0(duration / 60)} min file - " +
                       "the rest may have been skipped. Try the Thorough preset.");
        }
        var spoken = cues.Sum(c => c.End - c.Start);
        if (duration > 600 && spoken < duration * 0.02)
            output.Add("Very little speech was found for a file this long; results may be incomplete.");
        return output;
    }

    private static string WordKey(Word w) => NotKeyChar().Replace(w.Text.ToLowerInvariant(), "");

    /// <summary>Lower case without punctuation, as DropJunk compares cues.</summary>
    internal static string Normalize(string text) =>
        Py.Strip(NotNormChar().Replace(text.ToLowerInvariant().Replace(".", ""), ""));

    private static bool EndsSentence(string text) => Py.RStrip(text) is [.., '.' or '?' or '!'];

    private static string Joined(IEnumerable<Word> words) => string.Concat(words.Select(x => x.Text));

    private static int Count(string s, char c) => s.Count(x => x == c);

    [GeneratedRegex("([A-Za-z])\\1{3,}")]
    private static partial Regex Stretch();

    [GeneratedRegex("[^a-z0-9']+")]
    private static partial Regex NotKeyChar();

    [GeneratedRegex("[^a-z0-9' ]+")]
    private static partial Regex NotNormChar();
}
