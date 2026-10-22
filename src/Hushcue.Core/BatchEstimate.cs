namespace Hushcue.Core;

/// <summary>
/// How long a whole batch has left. The work is measured in seconds of media (each file's length times how far it is),
/// so a long file counts for more than a short one. Measured from the first report that shows any work done, so
/// starting up and loading the model don't count; until enough has been measured, a speed known from earlier runs on
/// this PC gives a first guess.
/// </summary>
public sealed class BatchEstimate(TimeProvider clock)
{
    // Before this much work the rate swings too much to be worth showing.
    private static readonly TimeSpan MinMeasured = TimeSpan.FromSeconds(3);
    private long _start;
    private double? _startWork;

    public BatchEstimate() : this(TimeProvider.System)
    {
    }

    /// <param name="files">each file's length in seconds (0 if unknown) and how far it is, 0..1</param>
    /// <param name="knownSpeed">times faster than real time on this PC, from earlier runs, for a first guess</param>
    /// <returns>null when there is nothing to go on yet</returns>
    public TimeSpan? TimeLeft(IReadOnlyCollection<(double Duration, double Fraction)> files, double? knownSpeed = null)
    {
        var total = files.Sum(f => Math.Max(f.Duration, 0));
        if (total <= 0)
            return null;
        var done = files.Sum(f => Math.Max(f.Duration, 0) * Math.Clamp(f.Fraction, 0, 1));
        var left = total - done;
        if (left <= 0)
            return TimeSpan.Zero;
        if (_startWork is null && done > 0)
            (_start, _startWork) = (clock.GetTimestamp(), done);
        if (_startWork is { } startWork)
        {
            var elapsed = clock.GetElapsedTime(_start);
            var measured = done - startWork;
            if (elapsed >= MinMeasured && measured > 0)
                return TimeSpan.FromSeconds(left * elapsed.TotalSeconds / measured);
        }
        return knownSpeed is > 0 ? TimeSpan.FromSeconds(left / knownSpeed.Value) : null;
    }

    /// <summary>"about 40 s", "about 9 min", "about 1 h 20 min".</summary>
    public static string Describe(TimeSpan left)
    {
        if (left.TotalSeconds < 60)
            return $"about {Math.Max(Math.Ceiling(left.TotalSeconds / 5) * 5, 5):F0} s";
        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        return minutes < 60 ? $"about {minutes} min" : $"about {minutes / 60} h {minutes % 60} min";
    }

    /// <summary>A length of media or time taken: "48 s", "3:00", "1:52:07".</summary>
    public static string Clock(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{Math.Round(span.TotalSeconds):F0} s"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}:{span.Seconds:00}"
        : $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
}
