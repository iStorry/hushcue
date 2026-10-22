using System.Text.Json;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core;

/// <summary>The choices the app remembers between runs, in %LOCALAPPDATA%\Hushcue\settings.json.</summary>
public sealed class AppSettings
{
    public string Preset { get; set; } = Pipeline.Presets.Default;
    public string Language { get; set; } = "English";
    public string Vocabulary { get; set; } = "";
    public EmbedMode Embed { get; set; } = EmbedMode.Soft;
    public bool KeepSrt { get; set; }
    public bool DeleteOriginal { get; set; }
    public bool ReviewBeforeEmbed { get; set; }
    public bool Enhance { get; set; }
    public bool SkipSilence { get; set; }
    public string OutputFolder { get; set; } = "";
    public bool ConvertSaveSrt { get; set; }
    public bool ConvertDeleteOriginal { get; set; }
    public string ConvertOutputFolder { get; set; } = "";
    public int Page { get; set; }

    /// <summary>"System" follows Windows; "Light" or "Dark" pins the app's theme.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Videos and subtitles opened in the editor, most recent first (at most <see cref="MaxRecentEdits"/>).</summary>
    public List<SubtitlePair> RecentEdits { get; set; } = [];

    public const int MaxRecentEdits = 8;

    /// <summary>Put <paramref name="pair"/> first in <see cref="RecentEdits"/>.</summary>
    public void AddRecentEdit(SubtitlePair pair)
    {
        RecentEdits.RemoveAll(p => string.Equals(p.Video, pair.Video, StringComparison.OrdinalIgnoreCase));
        RecentEdits.Insert(0, pair);
        if (RecentEdits.Count > MaxRecentEdits)
            RecentEdits.RemoveRange(MaxRecentEdits, RecentEdits.Count - MaxRecentEdits);
    }

    /// <summary>
    /// A job's settings from these choices. The options that only apply when subtitles go into the video (keep the
    /// .srt, recycle the original, review first) are off when <paramref name="embed"/> is None.
    /// </summary>
    public JobSettings ToJob(string? preset = null, EmbedMode? embed = null)
    {
        var mode = embed ?? Embed;
        var embedding = mode != EmbedMode.None;
        return new JobSettings
        {
            Preset = preset ?? Preset,
            Language = Presets.Languages.FirstOrDefault(l => l.Name == Language, Presets.Languages[0]).Code,
            Enhance = Enhance,
            Vocabulary = Vocabulary,
            SkipSilence = SkipSilence,
            Embed = mode,
            KeepSrt = embedding && KeepSrt,
            DeleteOriginal = embedding && DeleteOriginal,
            ReviewBeforeEmbed = embedding && ReviewBeforeEmbed,
        };
    }

    /// <summary>
    /// How many times faster than real time transcription ran on this PC, by preset and device ("Balanced/cuda"):
    /// the speed hint under the preset and the first guess of a batch's time left.
    /// </summary>
    public Dictionary<string, double> Speeds { get; set; } = new();

    public static string Path =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hushcue", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The saved settings, or defaults if there are none or they cannot be read.</summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? Path), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= Path;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // remembering choices is a convenience; never fail because of it
        }
    }
}
