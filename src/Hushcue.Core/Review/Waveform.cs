namespace Hushcue.Core.Review;

/// <summary>
/// The loudness outline of a file's audio for drawing a waveform: the lowest and highest sample of every
/// 1/<see cref="BucketsPerSecond"/> s, computed once, from which any stretch can be drawn at any width quickly.
/// </summary>
public sealed class Waveform
{
    public const int DefaultBucketsPerSecond = 100;

    private readonly float[] _min;
    private readonly float[] _max;

    private Waveform(float[] min, float[] max, int bucketsPerSecond, double duration, float peak)
    {
        (_min, _max, BucketsPerSecond, Duration, Peak) = (min, max, bucketsPerSecond, duration, peak);
    }

    public int BucketsPerSecond { get; }

    /// <summary>Seconds of audio.</summary>
    public double Duration { get; }

    /// <summary>The loudest sample (absolute), to scale the drawing; at least a small floor so silence stays flat.</summary>
    public float Peak { get; }

    public int Buckets => _min.Length;

    /// <param name="samples">mono audio</param>
    /// <param name="sampleRate">samples per second of <paramref name="samples"/></param>
    public static Waveform Build(ReadOnlySpan<float> samples, int sampleRate, int bucketsPerSecond = DefaultBucketsPerSecond)
    {
        var size = Math.Max(1, sampleRate / bucketsPerSecond);
        var buckets = (samples.Length + size - 1) / size;
        var min = new float[buckets];
        var max = new float[buckets];
        var peak = 0f;
        for (var b = 0; b < buckets; b++)
        {
            var chunk = samples.Slice(b * size, Math.Min(size, samples.Length - b * size));
            float lo = chunk[0], hi = chunk[0];
            foreach (var s in chunk)
            {
                if (s < lo)
                    lo = s;
                else if (s > hi)
                    hi = s;
            }
            (min[b], max[b]) = (lo, hi);
            peak = Math.Max(peak, Math.Max(-lo, hi));
        }
        return new Waveform(min, max, bucketsPerSecond, samples.Length / (double)sampleRate, Math.Max(peak, 0.01f));
    }

    /// <summary>
    /// The outline of [<paramref name="start"/>, <paramref name="end"/>) seconds in <paramref name="columns"/> columns:
    /// each column's lowest and highest sample, divided by <see cref="Peak"/> (so -1..1). Columns outside the audio are 0.
    /// </summary>
    public (float[] Min, float[] Max) Envelope(double start, double end, int columns)
    {
        var min = new float[Math.Max(columns, 0)];
        var max = new float[min.Length];
        if (columns <= 0 || end <= start)
            return (min, max);
        var perColumn = (end - start) / columns * BucketsPerSecond;
        for (var c = 0; c < columns; c++)
        {
            var first = (long)Math.Floor(start * BucketsPerSecond + c * perColumn);
            var last = Math.Max(first + 1, (long)Math.Floor(start * BucketsPerSecond + (c + 1) * perColumn));
            first = Math.Max(first, 0);
            last = Math.Min(last, _min.Length);
            if (first >= last)
                continue;
            float lo = 0, hi = 0;
            for (var b = first; b < last; b++)
            {
                lo = Math.Min(lo, _min[b]);
                hi = Math.Max(hi, _max[b]);
            }
            (min[c], max[c]) = (lo / Peak, hi / Peak);
        }
        return (min, max);
    }
}
