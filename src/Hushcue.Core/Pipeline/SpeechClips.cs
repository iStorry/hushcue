using Hushcue.Core.Engine;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// WhisperX's "VAD cut &amp; merge" (Bain et al. 2023): find the speech with Silero, and merge neighbouring stretches
/// into clips of up to <see cref="MaxClipSeconds"/>, the length Whisper was trained on. Each clip is decoded on its
/// own (faster-whisper's clip_timestamps), so no window edge falls inside a word, and silences of at least
/// <see cref="MinSkippedSilenceSeconds"/> between clips are never decoded, which is where Whisper invents text.
/// <list type="bullet">
/// <item>The speech is found generously (threshold 0.1, as Hushcue's "skip silence"), as audio outside every clip is
/// lost; clips closer together than that silence touch, so nothing between them is lost either.</item>
/// <item>Where two clips touch, the cut is at the first of the quietest windows in the <see cref="LeadInSeconds"/>
/// before the next speech, and a clip after a skipped silence starts that long before its speech: decoded from just
/// before a sentence, Whisper can skip the sentence.</item>
/// </list>
/// </summary>
public static class SpeechClips
{
    public const double MaxClipSeconds = 30;
    public const double LeadInSeconds = 1.0;
    public const double MinSkippedSilenceSeconds = 2.0;
    private const float QuietMargin = 0.02f;

    public static readonly VadOptions Options = new()
    {
        Threshold = 0.1,
        MinSilenceDurationMs = 500,
        SpeechPadMs = 200,
        MaxSpeechDurationS = MaxClipSeconds - LeadInSeconds,
    };

    /// <summary>
    /// The clips of audio of <paramref name="audioLength"/> samples to decode, as clip_timestamps (start, end, start,
    /// end, ... in seconds), given one speech probability per 512-sample window. Empty when no speech is found.
    /// </summary>
    public static List<double> CutAndMerge(float[] speechProbabilities, int audioLength)
    {
        const int rate = Media.Audio.SampleRate;
        var leadIn = (int)(LeadInSeconds * rate);
        var clips = new List<(int Start, int End)>();
        foreach (var chunk in SpeechTimestamps.FromProbabilities(speechProbabilities, audioLength, Options))
        {
            if (clips.Count == 0)
            {
                clips.Add((Math.Max(0, chunk.Start - leadIn), chunk.End));
            }
            else if (chunk.End - clips[^1].Start <= MaxClipSeconds * rate)
            {
                clips[^1] = (clips[^1].Start, chunk.End);
            }
            else if (chunk.Start - clips[^1].End >= MinSkippedSilenceSeconds * rate)
            {
                clips.Add((chunk.Start - leadIn, chunk.End));
            }
            else
            {
                var cut = QuietestBefore(speechProbabilities, Math.Max(clips[^1].Start + 1, chunk.Start - leadIn), chunk.Start);
                clips[^1] = (clips[^1].Start, cut);
                clips.Add((cut, chunk.End));
            }
        }
        return [.. clips.SelectMany(c => new[] { (double)c.Start / rate, (double)c.End / rate })];
    }

    /// <summary>The start (in samples) of the first window in [<paramref name="from"/>, <paramref name="to"/>) whose
    /// probability is within <see cref="QuietMargin"/> of the lowest there.</summary>
    private static int QuietestBefore(float[] speechProbabilities, int from, int to)
    {
        var first = Math.Min(from / SileroVad.WindowSamples, speechProbabilities.Length - 1);
        var last = Math.Clamp(to / SileroVad.WindowSamples, first + 1, speechProbabilities.Length);
        var window = speechProbabilities.AsSpan(first, last - first);
        var quietest = window[0];
        foreach (var probability in window)
            quietest = Math.Min(quietest, probability);
        return (first + window.IndexOfAnyInRange(float.MinValue, quietest + QuietMargin)) * SileroVad.WindowSamples;
    }
}
