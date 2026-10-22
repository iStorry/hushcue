using Hushcue.Core.Engine;
using Hushcue.Core.Media;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// A quality preset. All presets decode at temperature 0 only: Whisper's default fallback re-decodes hard segments
/// with random sampling, which made identical runs disagree on ~38% of the words when measured.
/// </summary>
/// <param name="Temperatures">always [0]: see above</param>
/// <param name="VadThreshold">null: no voice activity detection (Silero VAD drops breathy or quiet speech)</param>
/// <param name="MinSilenceMs">the VAD's minimum silence; 0 means 1000 ms</param>
public sealed record Preset(string Name, string Model, int BeamSize, IReadOnlyList<float> Temperatures,
                            double? VadThreshold, int MinSilenceMs, bool Enhance, string Description);

public static class Presets
{
    public static readonly IReadOnlyList<Preset> All =
    [
        new("Fast", "large-v3-turbo", 1, [0.0f], 0.1, 1000, false, "Clean audio, quickest results."),
        new("Balanced", "large-v3-turbo", 5, [0.0f], null, 0, false, "Good default for most videos."),
        new("Thorough", "large-v3", 5, [0.0f], null, 0, true, "Quiet / whispered / noisy audio. Slowest, most accurate."),
    ];

    public const string Default = "Balanced";

    /// <summary>The preset called <paramref name="name"/>; an unknown name (such as the dropped "Best") gets the default.</summary>
    public static Preset Get(string name) => All.FirstOrDefault(p => p.Name == name) ?? All.First(p => p.Name == Default);

    /// <summary>
    /// Voice activity detection runs when the preset has it or "skip silence" is on, always
    /// with threshold = the preset's or 0.1, minimum silence = the preset's or 1000 ms and speech padding 500 ms
    /// (faster-whisper's defaults for everything else). Null: no VAD.
    /// </summary>
    public static VadOptions? Vad(Preset preset, JobSettings settings) =>
        settings.SkipSilence || preset.VadThreshold is not null
            ? new VadOptions
            {
                Threshold = preset.VadThreshold ?? 0.1,
                MinSilenceDurationMs = preset.MinSilenceMs != 0 ? preset.MinSilenceMs : 1000,
                SpeechPadMs = 500,
            }
            : null;

    /// <summary>Display name to language code; null asks the model to detect it.</summary>
    public static readonly IReadOnlyList<(string Name, string? Code)> Languages = [("English", "en"), ("Auto-detect", null)];
}

public sealed record JobSettings
{
    public string Preset { get; init; } = Presets.Default;
    public string? Language { get; init; } = "en";
    /// <summary>Extra audio clean-up on top of the preset.</summary>
    public bool Enhance { get; init; }
    /// <summary>Names or words to spell correctly, given to Whisper as hotwords in every 30 s window.</summary>
    public string Vocabulary { get; init; } = "";
    /// <summary>Force voice activity detection on: faster, but can drop quiet or breathy speech.</summary>
    public bool SkipSilence { get; init; }
    /// <summary>Re-decode passages where Whisper looped, each on its own (<see cref="FileTranscription.RecoverLoops"/>);
    /// on by default.</summary>
    public bool RecoverLoops { get; init; } = true;
    /// <summary>Let each 30 s window see the text decoded before it (Whisper's condition_on_previous_text). Off by
    /// default: a window that goes wrong can pass its mistake on to the next.</summary>
    public bool PreviousText { get; init; }
    public EmbedMode Embed { get; init; } = EmbedMode.None;
    /// <summary>When embedding: also leave the .srt next to the video.</summary>
    public bool KeepSrt { get; init; } = true;
    /// <summary>When embedding: send the source video to the Recycle Bin afterwards.</summary>
    public bool DeleteOriginal { get; init; }
    /// <summary>When embedding: stop after writing the .srt (kept, whatever <see cref="KeepSrt"/> says) so the
    /// subtitles can be reviewed and edited first; the file ends as <see cref="FileState.Review"/> and is embedded
    /// later with <see cref="SubtitleJob.EmbedAsync(string, IReadOnlyList{Subtitles.Cue}, string?, EmbedMode, bool, Action{double}, Action{string}, CancellationToken)"/>.</summary>
    public bool ReviewBeforeEmbed { get; init; }
}
