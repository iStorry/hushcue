namespace Hushcue.Core.Pipeline;

/// <summary>
/// The status line while a file is transcribed: how many times faster than real time it goes and how long is left.
/// Measured from the first progress report, so loading the model and decoding the first window don't count.
/// </summary>
internal sealed class TranscribeRate(double audioSeconds, TimeProvider clock)
{
    // Before this much decoding the rate swings too much to be worth showing.
    private static readonly TimeSpan MinMeasured = TimeSpan.FromSeconds(2);
    private long _start;
    private double? _startFraction;

    /// <param name="fraction">how much of the audio is transcribed, 0..1</param>
    public string Describe(double fraction)
    {
        if (_startFraction is null)
            (_start, _startFraction) = (clock.GetTimestamp(), fraction);
        var elapsed = clock.GetElapsedTime(_start);
        var audioDone = (fraction - _startFraction.Value) * audioSeconds;
        if (elapsed < MinMeasured || audioDone <= 0)
            return $"Transcribing... {(int)(fraction * 100)}%";
        var speed = audioDone / elapsed.TotalSeconds;
        return $"Transcribing... {speed:F0}× real time, {TimeLeft(TimeSpan.FromSeconds((1 - fraction) * audioSeconds / speed))}";
    }

    internal static string TimeLeft(TimeSpan left) =>
        left.TotalSeconds < 60 ? $"about {Math.Ceiling(left.TotalSeconds):F0} s left" : $"about {Math.Ceiling(left.TotalMinutes):F0} min left";
}
