using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Engine;

/// <summary>faster-whisper's VadOptions, with its defaults.</summary>
public sealed record VadOptions
{
    /// <summary>Windows with a speech probability at or above this are speech.</summary>
    public double Threshold { get; init; } = 0.5;
    /// <summary>Below this, speech ends; null means 0.15 under <see cref="Threshold"/> (at least 0.01).</summary>
    public double? NegThreshold { get; init; }
    public int MinSpeechDurationMs { get; init; }
    public double MaxSpeechDurationS { get; init; } = double.PositiveInfinity;
    public int MinSilenceDurationMs { get; init; } = 2000;
    public int SpeechPadMs { get; init; } = 400;
}

/// <summary>A stretch of speech, in samples of the 16 kHz audio: <see cref="Start"/> inclusive, <see cref="End"/> exclusive.</summary>
public readonly record struct SpeechChunk(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// faster-whisper 1.2.1's vad.py around the model: speech chunks from window probabilities (get_speech_timestamps),
/// the speech cut out and joined (collect_chunks), and timestamps mapped back onto the original audio
/// (restore_speech_timestamps).
/// </summary>
public static class SpeechTimestamps
{
    private const int SampleRate = MelSpectrogram.SampleRate;
    private const double MinSilenceSamplesAtMaxSpeech = SampleRate * 98 / 1000.0;

    /// <summary>The speech in audio of <paramref name="audioLength"/> samples, given one probability per 512-sample window.</summary>
    public static List<SpeechChunk> FromProbabilities(ReadOnlySpan<float> probabilities, int audioLength, VadOptions options)
    {
        const int window = SileroVad.WindowSamples;
        var minSpeechSamples = SampleRate * options.MinSpeechDurationMs / 1000.0;
        var speechPadSamples = SampleRate * options.SpeechPadMs / 1000.0;
        var maxSpeechSamples = SampleRate * options.MaxSpeechDurationS - window - 2 * speechPadSamples;
        var minSilenceSamples = SampleRate * options.MinSilenceDurationMs / 1000.0;
        // NumPy 2 compares a float32 probability with a Python float in float32
        var threshold = (float)options.Threshold;
        var negThreshold = (float)(options.NegThreshold ?? Math.Max(options.Threshold - 0.15, 0.01));

        var speeches = new List<SpeechChunk>();
        var triggered = false;
        var open = false; // a speech chunk has a start but no end yet
        var start = 0;
        int tempEnd = 0, prevEnd = 0, nextStart = 0;
        for (var i = 0; i < probabilities.Length; i++)
        {
            var probability = probabilities[i];
            var sample = window * i;
            if (probability >= threshold && tempEnd != 0)
            {
                tempEnd = 0;
                if (nextStart < prevEnd)
                    nextStart = sample;
            }

            if (probability >= threshold && !triggered)
            {
                (triggered, open, start) = (true, true, sample);
                continue;
            }

            if (triggered && sample - start > maxSpeechSamples)
            {
                if (prevEnd != 0)
                {
                    speeches.Add(new SpeechChunk(start, prevEnd));
                    // previously reached silence (< negThreshold) and is still not speech (< threshold)
                    if (nextStart < prevEnd)
                        (triggered, open) = (false, false);
                    else
                        start = nextStart;
                    prevEnd = nextStart = tempEnd = 0;
                }
                else
                {
                    speeches.Add(new SpeechChunk(start, sample));
                    prevEnd = nextStart = tempEnd = 0;
                    (triggered, open) = (false, false);
                    continue;
                }
            }

            if (probability < negThreshold && triggered)
            {
                if (tempEnd == 0)
                    tempEnd = sample;
                // avoid cutting in a very short silence
                if (sample - tempEnd > MinSilenceSamplesAtMaxSpeech)
                    prevEnd = tempEnd;
                if (sample - tempEnd < minSilenceSamples)
                    continue;
                if (tempEnd - start > minSpeechSamples)
                    speeches.Add(new SpeechChunk(start, tempEnd));
                prevEnd = nextStart = tempEnd = 0;
                (triggered, open) = (false, false);
            }
        }

        if (open && audioLength - start > minSpeechSamples)
            speeches.Add(new SpeechChunk(start, audioLength));
        Pad(speeches, speechPadSamples, audioLength);
        return speeches;
    }

    /// <summary>Widen each chunk by the padding, sharing a gap shorter than two paddings equally between its neighbours.</summary>
    private static void Pad(List<SpeechChunk> speeches, double padSamples, int audioLength)
    {
        for (var i = 0; i < speeches.Count; i++)
        {
            var speech = speeches[i];
            if (i == 0)
                speech = speech with { Start = (int)Math.Max(0, speech.Start - padSamples) };
            if (i != speeches.Count - 1)
            {
                var next = speeches[i + 1];
                var silence = next.Start - speech.End;
                if (silence < 2 * padSamples)
                {
                    speech = speech with { End = speech.End + silence / 2 };
                    speeches[i + 1] = next with { Start = Math.Max(0, next.Start - silence / 2) };
                }
                else
                {
                    speech = speech with { End = (int)Math.Min(audioLength, speech.End + padSamples) };
                    speeches[i + 1] = next with { Start = (int)Math.Max(0, next.Start - padSamples) };
                }
            }
            else
            {
                speech = speech with { End = (int)Math.Min(audioLength, speech.End + padSamples) };
            }
            speeches[i] = speech;
        }
    }

    /// <summary>The speech chunks of <paramref name="audio"/>, joined end to end.</summary>
    public static float[] Collect(float[] audio, IReadOnlyList<SpeechChunk> chunks)
    {
        var kept = new float[chunks.Sum(c => c.Length)];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            audio.AsSpan(chunk.Start, chunk.Length).CopyTo(kept.AsSpan(offset));
            offset += chunk.Length;
        }
        return kept;
    }

    /// <summary>
    /// Move the timestamps of segments transcribed from <see cref="Collect"/>ed audio back to where they are in the
    /// original audio. Each word is placed by the chunk its middle falls in, so its start and end stay together.
    /// </summary>
    public static IEnumerable<Segment> Restore(IEnumerable<Segment> segments, IReadOnlyList<SpeechChunk> chunks)
    {
        var map = new SpeechTimestampsMap(chunks);
        foreach (var segment in segments)
        {
            if (segment.Words is { Count: > 0 } words)
            {
                List<TimedWord> restored = [.. words.Select(word =>
                {
                    var chunk = map.ChunkIndex((word.Start + word.End) / 2);
                    return new TimedWord(word.Text, map.OriginalTime(word.Start, chunk), map.OriginalTime(word.End, chunk),
                                         word.Probability);
                })];
                yield return segment with { Start = restored[0].Start, End = restored[^1].End, Words = restored };
            }
            else
            {
                yield return segment with
                {
                    Start = map.OriginalTime(segment.Start),
                    End = map.OriginalTime(segment.End, isEnd: true),
                };
            }
        }
    }
}

/// <summary>faster-whisper's SpeechTimestampsMap: from a time in the joined speech to the time in the original audio.</summary>
public sealed class SpeechTimestampsMap
{
    private const int SampleRate = MelSpectrogram.SampleRate;
    private const int TimePrecision = 2;

    private readonly int[] _chunkEndSample;
    private readonly double[] _totalSilenceBefore;

    public SpeechTimestampsMap(IReadOnlyList<SpeechChunk> chunks)
    {
        _chunkEndSample = new int[chunks.Count];
        _totalSilenceBefore = new double[chunks.Count];
        int previousEnd = 0, silentSamples = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            silentSamples += chunks[i].Start - previousEnd;
            previousEnd = chunks[i].End;
            _chunkEndSample[i] = chunks[i].End - silentSamples;
            _totalSilenceBefore[i] = silentSamples / (double)SampleRate;
        }
    }

    /// <param name="chunkIndex">the chunk <paramref name="time"/> belongs to; null finds it</param>
    public double OriginalTime(double time, int? chunkIndex = null, bool isEnd = false) =>
        Py.Round(_totalSilenceBefore[chunkIndex ?? ChunkIndex(time, isEnd)] + time, TimePrecision);

    /// <param name="isEnd">a time exactly at the end of a chunk belongs to that chunk, not the next one</param>
    public int ChunkIndex(double time, bool isEnd = false)
    {
        var sample = (int)(time * SampleRate);
        if (isEnd && Array.IndexOf(_chunkEndSample, sample) is >= 0 and var exact)
            return exact;
        return Math.Min(BisectRight(_chunkEndSample, sample), _chunkEndSample.Length - 1);
    }

    /// <summary><c>bisect.bisect</c>: the index after the last element not greater than <paramref name="value"/>.</summary>
    private static int BisectRight(int[] sorted, int value)
    {
        var (lo, hi) = (0, sorted.Length);
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (value < sorted[mid])
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }
}
