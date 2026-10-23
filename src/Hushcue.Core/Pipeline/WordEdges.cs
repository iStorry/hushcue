using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// Whisper's word times (cross-attention + DTW) often stretch a word over the silence next to it, most of all the
/// first word after a pause, which then starts too early and makes its subtitle appear before anyone speaks. As
/// stable-ts does, each word is trimmed to the speech Silero hears inside it: its start moves to the first window at
/// or above <see cref="SpeechProbability"/>, its end back to the last, with <see cref="PadSeconds"/> to spare and
/// never shorter than <see cref="MinWordSeconds"/>. A word with no speech under it is left alone: Silero misses some
/// quiet speech, and such words are for <see cref="UnheardCues"/> to judge.
/// </summary>
public static class WordEdges
{
    public const float SpeechProbability = 0.35f;
    public const double PadSeconds = SpeechWindows.Seconds;
    public const double MinWordSeconds = 0.1;

    /// <summary>The words with their edges trimmed, given one speech probability per 512-sample window of audio that
    /// starts <paramref name="origin"/> seconds into the words' time.</summary>
    public static List<Word> Trim(IReadOnlyList<Word> words, ReadOnlySpan<float> speechProbabilities, double origin = 0)
    {
        var trimmed = new List<Word>(words.Count);
        foreach (var word in words)
            trimmed.Add(TrimOne(word, speechProbabilities, origin));
        return trimmed;
    }

    private static Word TrimOne(Word word, ReadOnlySpan<float> speechProbabilities, double origin)
    {
        if (word.End - word.Start <= MinWordSeconds)
            return word;
        var (offset, length) = SpeechWindows.Under(word.Start - origin, word.End - origin, speechProbabilities.Length)
                                            .GetOffsetAndLength(speechProbabilities.Length);
        var under = speechProbabilities.Slice(offset, length);
        var first = under.IndexOfAnyInRange(SpeechProbability, float.MaxValue);
        if (first < 0)
            return word;
        var last = under.LastIndexOfAnyInRange(SpeechProbability, float.MaxValue);
        var start = Math.Max(word.Start, origin + (offset + first) * SpeechWindows.Seconds - PadSeconds);
        var end = Math.Min(word.End, origin + (offset + last + 1) * SpeechWindows.Seconds + PadSeconds);
        if (end - start < MinWordSeconds)
            start = Math.Max(word.Start, end - MinWordSeconds);
        if (end - start < MinWordSeconds)
            end = Math.Min(word.End, start + MinWordSeconds);
        return word with { Start = Math.Round(start, 3), End = Math.Round(end, 3) };
    }
}
