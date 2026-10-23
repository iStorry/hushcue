using Hushcue.Core.Engine;

namespace Hushcue.Core.Pipeline;

/// <summary>Seconds to Silero's 512-sample windows, for checking cues and words against its speech probabilities.</summary>
public static class SpeechWindows
{
    public const double Seconds = (double)SileroVad.WindowSamples / Media.Audio.SampleRate;

    /// <summary>The windows under [<paramref name="start"/>, <paramref name="end"/>) seconds, of
    /// <paramref name="count"/> windows from the start of the audio (empty outside it).</summary>
    public static Range Under(double start, double end, int count)
    {
        var first = Math.Clamp((int)(start / Seconds), 0, count);
        var last = Math.Clamp((int)Math.Ceiling(end / Seconds), first, count);
        return first..last;
    }
}
