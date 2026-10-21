using System.Text.Json;
using Hushcue.Core.Media;

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
    public bool Enhance { get; set; }
    public bool SkipSilence { get; set; }
    public string OutputFolder { get; set; } = "";
    public bool ConvertSaveSrt { get; set; }
    public bool ConvertDeleteOriginal { get; set; }
    public string ConvertOutputFolder { get; set; } = "";
    public int Page { get; set; }

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
