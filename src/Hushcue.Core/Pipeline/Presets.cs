using Hushcue.Core.Engine;
using Hushcue.Core.Media;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// A quality preset. All presets decode at temperature 0 only: Whisper's default fallback re-decodes hard segments
/// with random sampling, which made identical runs disagree on ~38% of the words in SubGen's measurements.
/// </summary>
/// <param name="Vad">null: no voice activity detection (Silero VAD drops breathy or quiet speech).</param>
public sealed record Preset(string Name, string Model, int BeamSize, VadOptions? Vad, bool Enhance, string Description);

public static class Presets
{
    public static readonly IReadOnlyList<Preset> All =
    [
        new("Fast", "large-v3-turbo", 1, new VadOptions { Threshold = 0.1, MinSilenceDurationMs = 1000 }, false,
            "Clean audio, quickest results."),
        new("Balanced", "large-v3-turbo", 5, null, false, "Good default for most videos."),
        new("Thorough", "large-v3", 5, null, true, "Quiet / whispered / noisy audio. Slowest, most accurate."),
    ];

    public const string Default = "Balanced";

    /// <summary>"Skip silent parts" on a preset without its own VAD: faster-whisper's vad_filter with default options.</summary>
    public static readonly VadOptions SkipSilenceVad = new();

    public static Preset Get(string name) => All.FirstOrDefault(p => p.Name == name) ?? All.First(p => p.Name == Default);

    /// <summary>Display name to language code; null asks the model to detect it.</summary>
    public static readonly IReadOnlyList<(string Name, string? Code)> Languages = [("English", "en"), ("Auto-detect", null)];
}

public sealed record JobSettings
{
    public string Preset { get; init; } = Presets.Default;
    public string? Language { get; init; } = "en";
    /// <summary>Extra audio clean-up on top of the preset.</summary>
    public bool Enhance { get; init; }
    /// <summary>Names or words to spell correctly, passed to Whisper as a prompt.</summary>
    public string Vocabulary { get; init; } = "";
    /// <summary>Force voice activity detection on: faster, but can drop quiet or breathy speech.</summary>
    public bool SkipSilence { get; init; }
    public EmbedMode Embed { get; init; } = EmbedMode.None;
    /// <summary>When embedding: also leave the .srt next to the video.</summary>
    public bool KeepSrt { get; init; } = true;
    /// <summary>When embedding: send the source video to the Recycle Bin afterwards.</summary>
    public bool DeleteOriginal { get; init; }
}
