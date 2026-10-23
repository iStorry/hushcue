using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Evaluation;

/// <summary>
/// A reference phrase and how far the transcript's words for it are off: their first start minus the phrase's start,
/// their last end minus its end, in seconds (positive: late). Null when no word was heard for the phrase.
/// </summary>
public sealed record PhraseTiming(Cue Reference, double? StartError, double? EndError);

public sealed record TimingScore(IReadOnlyList<PhraseTiming> Phrases)
{
    public IEnumerable<double> StartErrors => Phrases.Select(p => p.StartError).OfType<double>();

    public IEnumerable<double> EndErrors => Phrases.Select(p => p.EndError).OfType<double>();

    public int Heard => Phrases.Count(p => p.StartError is not null);
}

/// <summary>
/// How well word timing matches a reference whose cues are exact phrase times (eval/speaking-styles, or any
/// carefully timed subtitles), for <c>Hushcue.Cli timing</c>. Each word belongs to the phrase its middle is in or
/// nearest to; a word further than <see cref="MaxDistance"/> from every phrase belongs to none (an invented word).
/// </summary>
public static class TimingScoring
{
    public const double MaxDistance = 1.0;

    public static TimingScore Compare(IReadOnlyList<Cue> reference, IReadOnlyList<Word> words)
    {
        var heard = reference.Select(_ => new List<Word>()).ToList();
        foreach (var word in reference.Count > 0 ? words : [])
        {
            var middle = (word.Start + word.End) / 2;
            var (nearest, distance) = reference.Select((phrase, i) => (i, Distance(phrase, middle))).MinBy(p => p.Item2);
            if (distance <= MaxDistance)
                heard[nearest].Add(word);
        }
        return new TimingScore([.. reference.Select((phrase, i) => heard[i] is { Count: > 0 } mine
            ? new PhraseTiming(phrase, mine.Min(w => w.Start) - phrase.Start, mine.Max(w => w.End) - phrase.End)
            : new PhraseTiming(phrase, null, null))]);
    }

    /// <summary>The median of <paramref name="values"/> (0 for none).</summary>
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
            return 0;
        var half = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[half] : (sorted[half - 1] + sorted[half]) / 2;
    }

    private static double Distance(Cue phrase, double at) =>
        at < phrase.Start ? phrase.Start - at : at > phrase.End ? at - phrase.End : 0;
}
