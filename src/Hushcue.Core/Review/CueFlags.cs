using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Review;

public enum FlagKind
{
    /// <summary>A phrase Whisper writes over silence or music ("Thanks for watching!").</summary>
    Hallucination,
    /// <summary>A word or phrase repeated, or the cue before said again: Whisper may have looped.</summary>
    Repeat,
    /// <summary>A long stretch with no subtitles.</summary>
    Gap,
    /// <summary>The whole file looks empty or cut short (<see cref="Cleanup.CoverageWarnings"/>).</summary>
    Coverage,
}

/// <summary>Something worth checking in the subtitles.</summary>
/// <param name="Start">where it starts, in seconds</param>
/// <param name="Cue">the cue it is about, or null for a gap or the whole file</param>
/// <param name="Message">what to check, for people</param>
public sealed record CueFlag(FlagKind Kind, double Start, double End, int? Cue, string Message)
{
    /// <summary>A short label for a chip: "likely hallucination", "repeat", "gap", "coverage".</summary>
    public string Label => Kind switch
    {
        FlagKind.Hallucination => "likely hallucination",
        FlagKind.Repeat => "repeat",
        FlagKind.Gap => "gap",
        _ => "check",
    };
}

/// <summary>
/// Where a transcript most likely needs a person to look: phrases Whisper invents (the clean-up's hallucination
/// list, also inside a longer cue), loops the clean-up capped but kept, cues said twice, long stretches without
/// subtitles, and the coverage warnings the job gives for the whole file.
/// </summary>
public static class CueFlags
{
    /// <summary>Silence at least this long between subtitles is flagged as a gap.</summary>
    public const double MinGapSeconds = 20;

    // A word said this many times in a row in one cue is flagged as a repeat ("no, no, no" is ordinary speech); a two
    // or three word phrase from 3. The clean-up keeps 3 repeats of a loop it caps.
    private static readonly int[] MinRepeats = [0, 4, 3, 3];

    /// <param name="duration">the file's length in seconds</param>
    public static List<CueFlag> Find(IReadOnlyList<Cue> cues, double duration)
    {
        var flags = new List<CueFlag>();
        string? previous = null;
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var text = Cleanup.Normalize(cue.Text);
            if (IsHallucination(text))
                flags.Add(new CueFlag(FlagKind.Hallucination, cue.Start, cue.End, i,
                                      "Whisper often writes this over silence or music; check it was said"));
            else if (Loops(text))
                flags.Add(new CueFlag(FlagKind.Repeat, cue.Start, cue.End, i, "Repeated words: Whisper may have looped here"));
            else if (text.Length > 0 && text == previous)
                flags.Add(new CueFlag(FlagKind.Repeat, cue.Start, cue.End, i, "Says the same as the subtitle before it"));
            previous = text;
        }

        var coverage = Cleanup.CoverageWarnings(cues, duration);
        var lastEnd = cues.Count > 0 ? cues[^1].End : 0;
        var edges = new List<double> { 0 };
        foreach (var cue in cues)
            edges.AddRange([cue.Start, cue.End]);
        edges.Add(Math.Max(duration, lastEnd));
        for (var i = 0; i + 1 < edges.Count; i += 2)
        {
            var (start, end) = (edges[i], edges[i + 1]);
            var tail = i + 2 == edges.Count;
            if (end - start >= MinGapSeconds && !(tail && coverage.Count > 0))
                flags.Add(new CueFlag(FlagKind.Gap, start, end, null, $"No subtitles for {CueTime.Length(end - start)}"));
        }
        foreach (var warning in coverage)
            flags.Add(new CueFlag(FlagKind.Coverage, lastEnd, Math.Max(duration, lastEnd), null, warning));
        return [.. flags.OrderBy(f => f.Start)];
    }

    /// <summary>A known hallucination phrase is the whole cue or part of it (at word boundaries).</summary>
    public static bool IsHallucination(string normalized) =>
        Cleanup.Hallucinations.Any(phrase => $" {normalized} ".Contains($" {phrase} ", StringComparison.Ordinal));

    /// <summary>A word said 4 times in a row, or a 2-3 word phrase 3 times.</summary>
    private static bool Loops(string normalized)
    {
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var k = 1; k <= 3; k++)
        {
            for (var i = 0; i + k * MinRepeats[k] <= words.Length; i++)
            {
                var reps = 1;
                while (i + (reps + 1) * k <= words.Length
                       && words.AsSpan(i + reps * k, k).SequenceEqual(words.AsSpan(i, k)))
                    reps++;
                if (reps >= MinRepeats[k])
                    return true;
            }
        }
        return false;
    }
}
