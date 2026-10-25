using Hushcue.Core.Media;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void JobSettingsComeFromTheSettings()
    {
        var settings = new AppSettings
        {
            Preset = "Fast", Language = "Auto-detect", Vocabulary = "Anna", Enhance = true, SkipSilence = true,
            Embed = EmbedMode.Soft, KeepSrt = true, DeleteOriginal = true, PreviewFirst = true,
        };
        var job = settings.ToJob();
        Assert.Equal("Fast", job.Preset);
        Assert.Null(job.Language); // auto-detect
        Assert.Equal("Anna", job.Vocabulary);
        Assert.True(job.Enhance && job.SkipSilence && job.KeepSrt && job.DeleteOriginal && job.PreviewFirst);
        Assert.Equal("Balanced", settings.ToJob(preset: "Balanced").Preset);
    }

    [Fact]
    public void FileOptionsOnlyApplyWhenSubtitlesGoIntoTheVideo()
    {
        var settings = new AppSettings { KeepSrt = true, DeleteOriginal = true, PreviewFirst = true, Language = "English" };
        var job = settings.ToJob(embed: EmbedMode.None);
        Assert.False(job.KeepSrt || job.DeleteOriginal); // an .srt-only run never recycles
        Assert.True(job.PreviewFirst); // but can be previewed like any other
        Assert.Equal("en", job.Language);
    }

    [Fact]
    public void RecentEditsKeepTheLatestFirstWithoutDuplicates()
    {
        var settings = new AppSettings();
        for (var i = 0; i < AppSettings.MaxRecentEdits + 3; i++)
            settings.AddRecentEdit(new SubtitlePair($"v{i}.mp4", $"v{i}.srt"));
        settings.AddRecentEdit(new SubtitlePair("V5.MP4", "v5.en.srt")); // same video again, other case
        Assert.Equal(AppSettings.MaxRecentEdits, settings.RecentEdits.Count);
        Assert.Equal("v5.en.srt", settings.RecentEdits[0].Srt);
        Assert.Single(settings.RecentEdits, p => string.Equals(p.Video, "v5.mp4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThemeAndRecentEditsSurviveSaving()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hushcue_settings_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings { Theme = "Dark" };
            settings.AddRecentEdit(new SubtitlePair("a.mp4", "a.srt"));
            settings.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal("Dark", loaded.Theme);
            Assert.Equal([new SubtitlePair("a.mp4", "a.srt")], loaded.RecentEdits);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
