using System.Diagnostics;
using Hushcue.Core;

namespace Hushcue.Services;

/// <summary>
/// Measures how fast transcription runs on this PC from the files' progress: from the first "Transcribing" report (the
/// model is loaded and the first window decoded by then, as <c>TranscribeRate</c> measures) to "Writing SRT", which
/// the job reports as soon as the transcript is done. The speed is kept in the settings, by preset and device.
/// </summary>
internal sealed class SpeedMeter
{
    // Status texts from Core's JobRun / TranscribeRate that mark the start and end of transcribing a file.
    private const string TranscribingPrefix = "Transcribing";
    private const string WritingSrtPrefix = "Writing SRT";
    // Shorter files are mostly start-up and don't say much about the speed.
    private const double MinAudioSeconds = 20;

    private readonly Dictionary<string, long> _started = new(StringComparer.OrdinalIgnoreCase);
    private double _audio;
    private double _seconds;

    public static string Key(string preset, string device) => $"{preset}/{device}";

    /// <param name="duration">the file's length in seconds, 0 when unknown</param>
    public void Observe(FileProgress progress, double duration)
    {
        if (progress.State != FileState.Working)
            return;
        if (progress.Status.StartsWith(TranscribingPrefix, StringComparison.Ordinal))
        {
            _started.TryAdd(progress.Path, Stopwatch.GetTimestamp());
        }
        else if (progress.Status.StartsWith(WritingSrtPrefix, StringComparison.Ordinal)
                 && _started.Remove(progress.Path, out var start) && duration >= MinAudioSeconds)
        {
            _audio += duration;
            _seconds += Stopwatch.GetElapsedTime(start).TotalSeconds;
        }
    }

    /// <summary>Times faster than real time over the files measured, or null if none was.</summary>
    public double? Speed => _seconds > 0.5 ? _audio / _seconds : null;

    /// <summary>"~52× real time on your GPU" from a speed remembered in the settings.</summary>
    public static string Describe(double speed, string device) =>
        $"~{(speed >= 10 ? Math.Round(speed) : Math.Round(speed, 1))}× real time on your {(device == "cuda" ? "GPU" : "CPU")}";
}
