using Hushcue.Core.Engine;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// Whisper's commonest invention: a short cue ("you", "Thank you.") over noise, music, tones or silence. Silero VAD
/// tells whether anyone speaks under a cue; a cue of a few words with no window above
/// <see cref="SpeechProbability"/> is dropped. Longer cues are kept: Silero misses some quiet or breathy speech, and
/// a long invented passage is rare and caught by the loop and phrase checks instead.
/// </summary>
public static class UnheardCues
{
    public const int MaxWords = 3;
    public const float SpeechProbability = 0.2f;

    private const double WindowSeconds = (double)SileroVad.WindowSamples / Media.Audio.SampleRate;

    /// <summary>The cues worth keeping, given one speech probability per 512-sample window of the audio.</summary>
    public static List<Cue> Drop(IReadOnlyList<Cue> cues, ReadOnlySpan<float> speechProbabilities)
    {
        var kept = new List<Cue>(cues.Count);
        foreach (var cue in cues)
        {
            if (Py.Split(cue.Text).Length > MaxWords || Heard(cue, speechProbabilities))
                kept.Add(cue);
        }
        return kept;
    }

    private static bool Heard(Cue cue, ReadOnlySpan<float> speechProbabilities)
    {
        var first = Math.Clamp((int)(cue.Start / WindowSeconds), 0, speechProbabilities.Length);
        var last = Math.Clamp((int)Math.Ceiling(cue.End / WindowSeconds), first, speechProbabilities.Length);
        foreach (var probability in speechProbabilities[first..last])
        {
            if (probability >= SpeechProbability)
                return true;
        }
        return false;
    }
}
