namespace Hushcue.Core.Pipeline;

/// <summary>How fast a file is being transcribed; Speed and TimeLeft are null until there is enough to measure.</summary>
/// <param name="Fraction">how much of the audio is transcribed, 0..1</param>
/// <param name="Speed">times faster than real time</param>
internal sealed record RateReading(double Fraction, double? Speed, TimeSpan? TimeLeft)
{
    /// <summary>The status line: "Transcribing · 52× real time · about 40 s left", or the percentage until measured.</summary>
    public string Describe() =>
        Speed is { } speed && TimeLeft is { } left
            ? $"Transcribing · {speed:F0}× real time · {TranscribeRate.TimeLeft(left)}"
            : $"Transcribing · {(int)(Fraction * 100)}%";
}

/// <summary>
/// How many times faster than real time a file is transcribed and how long is left. Measured from the first progress
/// report, so loading the model and decoding the first window don't count.
/// </summary>
internal sealed class TranscribeRate(double audioSeconds, TimeProvider clock)
{
    // Before this much decoding the rate swings too much to be worth showing.
    private static readonly TimeSpan MinMeasured = TimeSpan.FromSeconds(2);
    private long _start;
    private double? _startFraction;

    /// <param name="fraction">how much of the audio is transcribed, 0..1</param>
    public RateReading Measure(double fraction)
    {
        if (_startFraction is null)
            (_start, _startFraction) = (clock.GetTimestamp(), fraction);
        var elapsed = clock.GetElapsedTime(_start);
        var audioDone = (fraction - _startFraction.Value) * audioSeconds;
        if (elapsed < MinMeasured || audioDone <= 0)
            return new RateReading(fraction, null, null);
        var speed = audioDone / elapsed.TotalSeconds;
        return new RateReading(fraction, speed, TimeSpan.FromSeconds((1 - fraction) * audioSeconds / speed));
    }

    internal static string TimeLeft(TimeSpan left) =>
        left.TotalSeconds < 60 ? $"about {Math.Ceiling(left.TotalSeconds):F0} s left" : $"about {Math.Ceiling(left.TotalMinutes):F0} min left";
}
