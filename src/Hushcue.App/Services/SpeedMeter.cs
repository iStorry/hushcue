using Hushcue.Core.Pipeline;

namespace Hushcue.Services;

/// <summary>
/// How fast transcription runs on this PC, from the time Core measured for each file (from the start of decoding, so
/// loading the model doesn't count, until the subtitles were ready). The speed is kept in the settings, by preset and
/// device.
/// </summary>
internal sealed class SpeedMeter
{
    // Shorter files are mostly start-up and don't say much about the speed.
    private const double MinAudioSeconds = 20;

    private double _audio;
    private double _seconds;

    public static string Key(string preset, string device) => $"{preset}/{device}";

    public void Observe(FileTranscribed file)
    {
        if (file.AudioSeconds < MinAudioSeconds)
            return;
        _audio += file.AudioSeconds;
        _seconds += file.Took.TotalSeconds;
    }

    /// <summary>Times faster than real time over the files measured, or null if none was.</summary>
    public double? Speed => _seconds > 0.5 ? _audio / _seconds : null;

    /// <summary>"~52× real time on your GPU" from a speed remembered in the settings.</summary>
    public static string Describe(double speed, string device) =>
        $"~{(speed >= 10 ? Math.Round(speed) : Math.Round(speed, 1))}× real time on your {(device == "cuda" ? "GPU" : "CPU")}";
}
