using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// Whisper's commonest inventions, checked against what Silero VAD hears under them:
/// <list type="bullet">
/// <item>a short cue ("you", "Thank you.") over noise, music, tones or silence: dropped when no window under it
/// reaches <see cref="SpeechProbability"/>. Longer cues are kept, as Silero misses some quiet or breathy speech;</item>
/// <item>a known phrase (<see cref="Cleanup.Hallucinations"/>, "Thanks for watching."): dropped unless speech is
/// clear under most of it (mean probability <see cref="ClearSpeechProbability"/>), as people do say these.</item>
/// </list>
/// </summary>
public static class UnheardCues
{
    public const int MaxWords = 3;
    public const float SpeechProbability = 0.2f;
    public const float ClearSpeechProbability = 0.5f;

    /// <summary>The cues worth keeping, given one speech probability per 512-sample window of audio that starts
    /// <paramref name="origin"/> seconds into the cues' time.</summary>
    public static List<Cue> Drop(IReadOnlyList<Cue> cues, ReadOnlySpan<float> speechProbabilities, double origin = 0)
    {
        var kept = new List<Cue>(cues.Count);
        foreach (var cue in cues)
        {
            var under = speechProbabilities[SpeechWindows.Under(cue.Start - origin, cue.End - origin, speechProbabilities.Length)];
            var unheard = Py.Split(cue.Text).Length <= MaxWords && !ContainsAtLeast(under, SpeechProbability);
            var invented = Cleanup.Hallucinations.Contains(Cleanup.Normalize(cue.Text)) && Mean(under) < ClearSpeechProbability;
            if (!unheard && !invented)
                kept.Add(cue);
        }
        return kept;
    }

    private static bool ContainsAtLeast(ReadOnlySpan<float> probabilities, float threshold)
    {
        foreach (var probability in probabilities)
        {
            if (probability >= threshold)
                return true;
        }
        return false;
    }

    private static float Mean(ReadOnlySpan<float> probabilities)
    {
        var sum = 0f;
        foreach (var probability in probabilities)
            sum += probability;
        return probabilities.IsEmpty ? 0 : sum / probabilities.Length;
    }
}
