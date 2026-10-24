using System.Diagnostics;
using System.Text.Json;
using Hushcue.Core.Media;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

/// <summary>A test that needs ffmpeg and ffprobe; skipped when they are not installed.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        try
        {
            _ = Tools.Ffmpeg;
            _ = Tools.Ffprobe;
        }
        catch (MediaException)
        {
            Skip = "needs ffmpeg";
        }
    }
}

/// <summary>Embedding, recycling and audio extraction, with real ffmpeg.</summary>
public sealed class MediaTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("hushcue_media_").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string P(string name) => Path.Combine(_tmp, name);

    private static string Run(params string[] args)
    {
        var info = new ProcessStartInfo(args[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args.Skip(1))
            info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors.Result);
        return output.Result;
    }

    private static byte[] RunBytes(params string[] args)
    {
        var info = new ProcessStartInfo(args[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args.Skip(1))
            info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        _ = process.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();
        return buffer.ToArray();
    }

    private static string MakeVideo(string path, int seconds = 4, string size = "640x360", string audio = "aac")
    {
        Run(Tools.Ffmpeg, "-y", "-v", "error", "-f", "lavfi", "-i", $"testsrc2=s={size}:d={seconds}:r=25", "-f", "lavfi",
            "-i", $"sine=d={seconds}", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", audio, "-shortest", path);
        return path;
    }

    private static string MakeSrt(string path)
    {
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:03,000\nHello there, friend\n");
        return path;
    }

    private static List<JsonElement> Streams(string path)
    {
        var json = JsonDocument.Parse(Run(Tools.Ffprobe, "-v", "error", "-print_format", "json", "-show_streams", path));
        return [.. json.RootElement.GetProperty("streams").EnumerateArray()];
    }

    private static Dictionary<string, JsonElement> ByType(string path)
    {
        var kinds = new Dictionary<string, JsonElement>();
        foreach (var s in Streams(path))
            kinds[s.GetProperty("codec_type").GetString()!] = s;
        return kinds;
    }

    /// <summary>Grey-scale bottom third of the frame at time t, where subtitles are drawn.</summary>
    private static byte[] BottomPixels(string path, double t, int width = 640, int height = 360)
    {
        var raw = RunBytes(Tools.Ffmpeg, "-v", "error", "-ss", t.ToString(System.Globalization.CultureInfo.InvariantCulture),
                           "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "-");
        Assert.Equal(width * height, raw.Length);
        return raw[(width * (height * 2 / 3))..];
    }

    private static double MeanAbsDiff(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    private static string VideoMd5(string path) =>
        Run(Tools.Ffmpeg, "-v", "error", "-i", path, "-map", "0:v:0", "-c", "copy", "-f", "md5", "-").Trim();

    // ------------------------------------------------------------------------------------------- pure

    [Fact]
    public void StyleScalesWithTheShorterSide()
    {
        Assert.StartsWith("15,", Embed.BurnStyle(1920, 1080).Split("FontSize=")[1]);
        Assert.StartsWith("15,", Embed.BurnStyle(3840, 2160).Split("FontSize=")[1]); // 4K: same proportion
        var portrait = double.Parse(Embed.BurnStyle(1080, 1920).Split("FontSize=")[1].Split(',')[0],
                                    System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(15.0 * 1080 / 1920, portrait, 6); // sized by width, not height
    }

    [Fact]
    public void AudioIsCopiedOnlyWhenMp4CanHoldIt()
    {
        Assert.Equal(["-c:a", "copy"], Embed.AudioArgs(["aac"]));
        Assert.Equal(["-c:a", "copy"], Embed.AudioArgs(["aac", "ac3"]));
        Assert.Equal(["-c:a", "aac"], Embed.AudioArgs(["opus"])[..2]);
        Assert.Equal(["-c:a", "aac"], Embed.AudioArgs(["aac", "flac"])[..2]);
        Assert.Equal(["-c:a", "copy"], Embed.AudioArgs([])); // silent video
    }

    [Fact]
    public void ParseProgress()
    {
        Assert.Equal(0.5, Tools.ParseProgress("out_time_us=5000000", 10));
        Assert.Equal(0.25, Tools.ParseProgress("out_time_ms=2500000", 10));
        Assert.Equal(1.0, Tools.ParseProgress("out_time_us=99000000", 10)); // clamped
        Assert.Null(Tools.ParseProgress("out_time_us=N/A", 10));
        Assert.Null(Tools.ParseProgress("frame=120", 10));
        Assert.Null(Tools.ParseProgress("out_time_us=5000000", 0));
    }

    [FfmpegFact]
    public void CommandsDropSourceSubtitleAndChapterTracks()
    {
        var info = new VideoInfo(1920, 1080, 10.0, ["aac"]);
        var burned = Embed.BuildCommand(EmbedMode.Burn, P("v.mp4"), "subs.srt", P("o.mp4"), info, Embed.X264Args);
        Assert.Contains("-sn", burned);
        Assert.Equal("-1", burned[burned.IndexOf("-map_chapters") + 1]);
        Assert.Contains(burned, a => a.StartsWith("subtitles=subs.srt:force_style='", StringComparison.Ordinal));
        var soft = Embed.BuildCommand(EmbedMode.Soft, P("v.mp4"), "subs.srt", P("o.mkv"), info);
        Assert.Equal("copy", soft[soft.IndexOf("-c") + 1]);
        Assert.DoesNotContain("libx264", soft);
        Assert.Throws<ArgumentException>(() => Embed.BuildCommand(EmbedMode.None, P("v.mp4"), "subs.srt", P("o.mp4"), info));
    }

    // ------------------------------------------------------------------------------------------- embedding

    [FfmpegFact]
    public async Task BurnDrawsTextOnlyWhileTheCueIsActive()
    {
        var (video, srt) = (MakeVideo(P("in.mp4")), MakeSrt(P("in.srt")));
        var fractions = new List<double>();
        var output = await Embed.SubtitlesAsync(video, srt, null, EmbedMode.Burn, fractions.Add, Embed.X264Args);
        Assert.Equal(P("in.subbed.mp4"), output);
        Assert.True(File.Exists(output) && File.Exists(video)); // original untouched
        var kinds = ByType(output);
        Assert.Equal("h264", kinds["video"].GetProperty("codec_name").GetString());
        Assert.Equal((640, 360), (kinds["video"].GetProperty("width").GetInt32(), kinds["video"].GetProperty("height").GetInt32()));
        Assert.Equal("aac", kinds["audio"].GetProperty("codec_name").GetString());
        Assert.False(kinds.ContainsKey("subtitle"));
        var during = MeanAbsDiff(BottomPixels(output, 2.0), BottomPixels(video, 2.0));
        var outside = MeanAbsDiff(BottomPixels(output, 3.6), BottomPixels(video, 3.6));
        Assert.True(during > 3 * outside && during > 2.0, $"during={during} outside={outside}"); // text only inside the cue
        Assert.NotEmpty(fractions);
        Assert.Equal(1.0, fractions[^1]);
        Assert.All(fractions.Zip(fractions.Skip(1)), p => Assert.True(p.First <= p.Second));
    }

    [FfmpegFact]
    public async Task BurnWorksWithWhateverEncoderThisMachinePicks()
    {
        var output = await Embed.SubtitlesAsync(MakeVideo(P("in.mp4")), MakeSrt(P("in.srt")), null, EmbedMode.Burn);
        Assert.True(new FileInfo(output).Length > 0);
        Assert.Equal("h264", Streams(output)[0].GetProperty("codec_name").GetString());
    }

    [FfmpegFact]
    public async Task SoftModeAddsATrackWithoutReencoding()
    {
        var (video, srt) = (MakeVideo(P("in.mp4")), MakeSrt(P("in.srt")));
        var output = await Embed.SubtitlesAsync(video, srt, P("out"), EmbedMode.Soft);
        Assert.Equal(Path.Combine(P("out"), "in.mkv"), output);
        var kinds = ByType(output);
        Assert.Equal("subrip", kinds["subtitle"].GetProperty("codec_name").GetString());
        Assert.Equal("eng", kinds["subtitle"].GetProperty("tags").GetProperty("language").GetString());
        Assert.Equal("h264", kinds["video"].GetProperty("codec_name").GetString());
        Assert.Equal("aac", kinds["audio"].GetProperty("codec_name").GetString());
        var decoded = Run(Tools.Ffprobe, "-v", "error", "-select_streams", "s:0", "-show_entries", "packet=pts_time",
                          "-of", "csv=p=0", output).Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(decoded);
        Assert.Equal(1.0, double.Parse(decoded[0], System.Globalization.CultureInfo.InvariantCulture), 0.05);
    }

    [FfmpegFact]
    public async Task AwkwardFileNamesDoNotBreakTheFilter()
    {
        var video = MakeVideo(P("Anna's [Live] - café 日本 (2023), part 1.mp4"));
        var output = await Embed.SubtitlesAsync(video, MakeSrt(P("x.srt")), null, EmbedMode.Burn, encoder: Embed.X264Args);
        Assert.True(File.Exists(output));
        Assert.EndsWith(".subbed.mp4", output);
    }

    [FfmpegFact]
    public async Task ExistingOutputIsNeverOverwritten()
    {
        var (video, srt) = (MakeVideo(P("in.mp4"), seconds: 1), MakeSrt(P("in.srt")));
        var first = await Embed.SubtitlesAsync(video, srt, null, EmbedMode.Burn, encoder: Embed.X264Args);
        var second = await Embed.SubtitlesAsync(video, srt, null, EmbedMode.Burn, encoder: Embed.X264Args);
        Assert.Equal("in.subbed.mp4", Path.GetFileName(first));
        Assert.Equal("in.subbed (1).mp4", Path.GetFileName(second));
    }

    [FfmpegFact]
    public async Task NonAacAudioIsConvertedForMp4()
    {
        var video = MakeVideo(P("in.mkv"), audio: "libopus");
        var output = await Embed.SubtitlesAsync(video, MakeSrt(P("in.srt")), null, EmbedMode.Burn, encoder: Embed.X264Args);
        Assert.Equal("aac", ByType(output)["audio"].GetProperty("codec_name").GetString());
    }

    [FfmpegFact]
    public async Task CancelStopsFfmpegAndRemovesThePartialFile()
    {
        var video = MakeVideo(P("in.mp4"), seconds: 30, size: "1920x1080");
        var srt = MakeSrt(P("in.srt"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(0.7));
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Embed.SubtitlesAsync(
            video, srt, null, EmbedMode.Burn, encoder: ["-c:v", "libx264", "-preset", "veryslow"], cancel: cancel.Token));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Empty(Directory.GetFiles(_tmp, "*.subbed*"));
    }

    [FfmpegFact]
    public async Task UnreadableInputFailsWithAClearMessage()
    {
        var bad = P("bad.mp4");
        File.WriteAllBytes(bad, "not a video"u8.ToArray());
        var error = await Assert.ThrowsAsync<MediaException>(() => Embed.SubtitlesAsync(bad, MakeSrt(P("x.srt")), null, EmbedMode.Burn));
        Assert.Contains("ffprobe could not read", error.Message);
        Assert.Empty(Directory.GetFiles(_tmp, "*.subbed*"));
    }

    [FfmpegFact]
    public async Task SoftOutputUsesThePlainNameAndOnlyAddsSubbedOnAClash()
    {
        var (video, srt) = (MakeVideo(P("in.mp4"), seconds: 1), MakeSrt(P("in.srt")));
        var first = await Embed.SubtitlesAsync(video, srt, null, EmbedMode.Soft);
        var second = await Embed.SubtitlesAsync(video, srt, null, EmbedMode.Soft);
        Assert.Equal("in.mkv", Path.GetFileName(first));
        Assert.Equal("in.subbed.mkv", Path.GetFileName(second));
    }

    // ------------------------------------------------------------------------------------------- mkv -> mp4

    private async Task<(string Video, string Mkv)> MkvWithSubs(string name = "in")
    {
        var (video, srt) = (MakeVideo(P($"{name}.mp4"), seconds: 2), MakeSrt(P($"{name}.srt")));
        return (video, await Embed.SubtitlesAsync(video, srt, P("mkv"), EmbedMode.Soft));
    }

    [FfmpegFact]
    public async Task StripSubtitlesGivesAnMp4WithTheSameVideoAndNoSubtitleTrack()
    {
        var (video, mkv) = await MkvWithSubs();
        Assert.Contains(Streams(mkv), s => s.GetProperty("codec_type").GetString() == "subtitle");
        var output = await Embed.StripSubtitlesAsync(mkv, P("back"));
        Assert.Equal(Path.Combine(P("back"), "in.mp4"), output);
        Assert.True(File.Exists(output) && File.Exists(mkv));
        var kinds = Streams(output).Select(s => s.GetProperty("codec_type").GetString()).ToList();
        Assert.DoesNotContain("subtitle", kinds);
        Assert.Single(kinds, k => k == "video");
        Assert.Single(kinds, k => k == "audio");
        Assert.Equal(VideoMd5(video), VideoMd5(output)); // picture untouched: no re-encode
    }

    [FfmpegFact]
    public async Task StripSubtitlesCanSaveTheSrtAndNeverOverwrites()
    {
        var (_, mkv) = await MkvWithSubs();
        var folder = P("back");
        var first = await Embed.StripSubtitlesAsync(mkv, folder, saveSrt: true);
        var second = await Embed.StripSubtitlesAsync(mkv, folder);
        Assert.Equal("in.mp4", Path.GetFileName(first));
        Assert.Equal("in (1).mp4", Path.GetFileName(second));
        Assert.Contains("hello", File.ReadAllText(Path.Combine(folder, "in.srt")).ToLowerInvariant());
    }

    [FfmpegFact]
    public async Task StripSubtitlesOnAFileWithNoSubtitlesStillWorks()
    {
        var video = MakeVideo(P("plain.mp4"), seconds: 1);
        var mkv = P("plain.mkv");
        Run(Tools.Ffmpeg, "-y", "-v", "error", "-i", video, "-c", "copy", mkv);
        var output = await Embed.StripSubtitlesAsync(mkv, null, saveSrt: true);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(P("plain.srt"))); // no track -> no srt, not an error
    }

    [FfmpegFact]
    public async Task ConvertFilesRecyclesOriginalsOnlyAfterSuccessAndSkipsBadFiles()
    {
        var (_, good) = await MkvWithSubs();
        var bad = P("broken.mkv");
        File.WriteAllBytes(bad, "not a video"u8.ToArray());
        var recycled = new List<string>();
        var finished = new List<FileProgress>();
        var error = await Assert.ThrowsAsync<MediaException>(() => Embed.ConvertFilesAsync(
            [bad, good], null, saveSrt: false, deleteOriginal: true, (_, _) => { }, _ => { }, CancellationToken.None,
            onFile: p => { if (p.State is FileState.Done or FileState.Failed) finished.Add(p); },
            recycle: path => { recycled.Add(Path.GetFileName(path)); return true; }));
        Assert.Contains("broken.mkv", error.Message);
        Assert.Equal(["in.mkv"], recycled); // the bad one is left alone
        var mp4 = Path.Combine(Path.GetDirectoryName(good)!, "in.mp4");
        Assert.Equal([(bad, FileState.Failed, null), (good, FileState.Done, mp4)],
                     finished.Select(p => (p.Path, p.State, p.Output)));
        Assert.True(File.Exists(mp4));
    }

    // ------------------------------------------------------------------------------------------- subtitle tracks

    [Fact]
    public void SubtitleTracksAreReadFromFfprobe()
    {
        var tracks = SubtitleTracks.Parse("""
            {"streams": [
              {"index": 0, "codec_type": "video", "codec_name": "h264"},
              {"index": 2, "codec_type": "subtitle", "codec_name": "subrip", "tags": {"language": "eng", "title": "SDH"},
               "disposition": {"default": 1, "forced": 0}},
              {"index": 3, "codec_type": "subtitle", "codec_name": "ass", "tags": {"language": "ger"}, "disposition": {"forced": 1}},
              {"index": 4, "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle"}
            ]}
            """);
        Assert.Equal([2, 3, 4], tracks.Select(t => t.Index));
        Assert.Equal([true, true, false], tracks.Select(t => t.IsText));
        Assert.Equal("English · SDH · default · SRT", tracks[0].Label);
        Assert.Equal("German · forced · ASS", tracks[1].Label);
        Assert.Equal("Unknown language · Blu-ray PGS", tracks[2].Label);
        Assert.Empty(SubtitleTracks.Parse("""{"streams": []}"""));
        Assert.Empty(SubtitleTracks.Parse("{}"));
    }

    [Fact]
    public void TrackLanguagesAreNamedFromEitherCode()
    {
        Assert.Equal("English", SubtitleTrack.LanguageName("en"));
        Assert.Equal("French", SubtitleTrack.LanguageName("fre"));
        Assert.Equal("French", SubtitleTrack.LanguageName("fra"));
        Assert.Null(SubtitleTrack.LanguageName("und"));
        Assert.Null(SubtitleTrack.LanguageName(null));
        Assert.Equal("xx-made-up", SubtitleTrack.LanguageName("xx-made-up")); // unknown: as written
    }

    [Fact]
    public void ATrackIsSavedNextToTheVideoByLanguage()
    {
        var video = P("film.mkv");
        Assert.Equal(P("film.eng.srt"), SubtitleTracks.SrtPath(video, new SubtitleTrack(2, "subrip", "eng", null, false, false)));
        Assert.Equal(P("film.srt"), SubtitleTracks.SrtPath(video, new SubtitleTrack(2, "subrip", "und", null, false, false)));
        Assert.Equal(P("film.srt"), SubtitleTracks.SrtPath(video, new SubtitleTrack(2, "subrip", null, null, false, false)));
        Assert.Equal(P("film.x.srt"), SubtitleTracks.SrtPath(video, new SubtitleTrack(2, "subrip", "../x", null, false, false)));
        File.WriteAllText(P("film.eng.srt"), "");
        Assert.Equal(P("film.eng (1).srt"), SubtitleTracks.SrtPath(video, new SubtitleTrack(2, "subrip", "eng", null, false, false)));
    }

    [Fact]
    public async Task PictureTracksAreNotExtracted()
    {
        var error = await Assert.ThrowsAsync<MediaException>(() => SubtitleTracks.ExtractAsync(
            P("film.mkv"), new SubtitleTrack(2, "hdmv_pgs_subtitle", null, null, false, false), P("film.srt")));
        Assert.Contains("Blu-ray PGS", error.Message);
        Assert.False(File.Exists(P("film.srt")));
    }

    [FfmpegFact]
    public async Task EachTextTrackOfAnMkvCanBeSavedAsAnSrt()
    {
        var video = MakeVideo(P("in.mp4"), seconds: 2);
        var english = MakeSrt(P("en.srt"));
        var german = P("de.srt");
        File.WriteAllText(german, "1\n00:00:00,500 --> 00:00:01,500\nHallo zusammen\n");
        var mkv = P("film.mkv");
        Run(Tools.Ffmpeg, "-y", "-v", "error", "-i", video, "-i", english, "-i", german, "-map", "0", "-map", "1", "-map", "2",
            "-c", "copy", "-c:s:1", "ass", "-metadata:s:s:0", "language=eng", "-metadata:s:s:1", "language=ger", mkv);

        var tracks = await SubtitleTracks.ReadAsync(mkv);
        Assert.Equal(["subrip", "ass"], tracks.Select(t => t.Codec));
        Assert.Equal(["eng", "ger"], tracks.Select(t => t.Language));
        Assert.All(tracks, t => Assert.True(t.IsText));

        var srt = SubtitleTracks.SrtPath(mkv, tracks[1]);
        Assert.Equal(P("film.ger.srt"), srt);
        await SubtitleTracks.ExtractAsync(mkv, tracks[1], srt);
        var cue = Assert.Single(Srt.Read(File.ReadAllText(srt)));
        Assert.Equal((0.5, 1.5, "Hallo zusammen"), (cue.Start, cue.End, cue.Text)); // the ASS track, back as SubRip
        Assert.Equal(srt, SubtitlePairs.FindSubtitles(mkv)); // found by name next time
        Assert.Empty(await SubtitleTracks.ReadAsync(video));
    }

    [FfmpegFact]
    public async Task AnEmptyTrackLeavesNoSrtBehind()
    {
        var video = MakeVideo(P("in.mp4"), seconds: 1);
        var empty = P("empty.srt");
        File.WriteAllText(empty, "");
        var mkv = P("film.mkv");
        Run(Tools.Ffmpeg, "-y", "-v", "error", "-i", video, "-f", "srt", "-i", empty, "-map", "0", "-map", "1", "-c", "copy", mkv);
        var track = Assert.Single(await SubtitleTracks.ReadAsync(mkv));
        var srt = SubtitleTracks.SrtPath(mkv, track);
        await Assert.ThrowsAsync<MediaException>(() => SubtitleTracks.ExtractAsync(mkv, track, srt));
        Assert.False(File.Exists(srt));
    }

    // ------------------------------------------------------------------------------------------- audio

    [FfmpegFact]
    public async Task AudioIsRead16kHzMonoFloat()
    {
        var samples = await Audio.ReadAsync(MakeVideo(P("in.mp4"), seconds: 2));
        Assert.InRange(samples.Length, 2 * 16000 - 1600, 2 * 16000 + 1600);
        Assert.Contains(samples, s => Math.Abs(s) > 0.01f); // the sine tone is there
        Assert.All(samples, s => Assert.InRange(s, -1.0f, 1.0f));
        var enhanced = await Audio.ReadAsync(P("in.mp4"), enhance: true);
        Assert.InRange(enhanced.Length, 2 * 16000 - 1600, 2 * 16000 + 1600);
    }

    [FfmpegFact]
    public async Task PartOfTheAudioIsReadFromItsStart()
    {
        var whole = await Audio.ReadAsync(MakeVideo(P("in.mp4"), seconds: 4));
        var part = await Audio.ReadPartAsync(P("in.mp4"), 1.5, 2);
        Assert.InRange(part.Length, 2 * 16000 - 800, 2 * 16000 + 800);
        // the same samples as the whole file's from 1.5 s, give or take the decoder's edge
        var offset = (int)(1.5 * 16000);
        var differences = Enumerable.Range(1000, 16000).Count(i => Math.Abs(part[i] - whole[offset + i]) > 0.01f);
        Assert.True(differences < 160, $"{differences} samples differ");
        var enhanced = await Audio.ReadPartAsync(P("in.mp4"), 3, 5, enhance: true); // cut short at the end of the file
        Assert.InRange(enhanced.Length, 16000 - 800, 16000 + 800);
    }

    [FfmpegFact]
    public async Task UnreadableAudioFailsWithAClearMessage()
    {
        var bad = P("bad.mp3");
        File.WriteAllBytes(bad, "not audio"u8.ToArray());
        var error = await Assert.ThrowsAsync<MediaException>(() => Audio.ReadAsync(bad));
        Assert.StartsWith("ffmpeg could not read audio from this file.", error.Message);
    }
}
