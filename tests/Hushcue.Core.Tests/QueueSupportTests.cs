using System.Diagnostics;
using System.Text;
using Hushcue.Core.Media;

namespace Hushcue.Core.Tests;

/// <summary>What the app's queue uses: probing files as they are added, the batch's time left, and undoing the Recycle Bin.</summary>
public sealed class QueueSupportTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("hushcue_queue_").FullName;

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

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private string MakeMedia(string name, params string[] inputs)
    {
        var path = Path.Combine(_tmp, name);
        var info = new ProcessStartInfo(Tools.Ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-y", "-v", "error" }.Concat(inputs).Append(path))
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors);
        return path;
    }

    [FfmpegFact]
    public async Task ProbeFindsTheStreamsAndLength()
    {
        var video = MakeMedia("both.mp4", "-f", "lavfi", "-i", "testsrc2=s=320x240:d=3:r=25", "-f", "lavfi", "-i", "sine=d=3",
                              "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
        var silent = MakeMedia("silent.mp4", "-f", "lavfi", "-i", "testsrc2=s=320x240:d=2:r=25", "-c:v", "libx264",
                               "-pix_fmt", "yuv420p");
        var audio = MakeMedia("audio.wav", "-f", "lavfi", "-i", "sine=d=2");

        var both = await MediaProbe.ReadAsync(video);
        Assert.True(both is { HasVideo: true, HasAudio: true });
        Assert.InRange(both.Duration, 2.9, 3.2);
        Assert.Equal((true, false), ((await MediaProbe.ReadAsync(silent)).HasVideo, (await MediaProbe.ReadAsync(silent)).HasAudio));
        var sound = await MediaProbe.ReadAsync(audio);
        Assert.Equal((false, true), (sound.HasVideo, sound.HasAudio));
        Assert.InRange(sound.Duration, 1.9, 2.1);
    }

    [FfmpegFact]
    public async Task ProbeRejectsAFileThatIsNotMedia()
    {
        var text = Path.Combine(_tmp, "notes.mp4");
        await File.WriteAllTextAsync(text, "not a video");
        var e = await Assert.ThrowsAsync<MediaException>(() => MediaProbe.ReadAsync(text));
        Assert.StartsWith("Not a video or audio file", e.Message);
    }

    [Fact]
    public void ProbeDoesNotCountCoverArtAsVideo()
    {
        const string json = """
            {"streams": [{"codec_type": "audio"}, {"codec_type": "video", "disposition": {"attached_pic": 1}}],
             "format": {"duration": "61.5"}}
            """;
        Assert.Equal(new MediaInfo(61.5, false, true), MediaProbe.Parse(json));
    }

    [Fact]
    public void BatchEstimateUsesTheKnownSpeedUntilItHasMeasuredEnough()
    {
        var clock = new ManualClock();
        var estimate = new BatchEstimate(clock);
        (double, double)[] files = [(600, 0), (300, 0)];

        Assert.Null(estimate.TimeLeft(files));
        Assert.Equal(TimeSpan.FromSeconds(18), estimate.TimeLeft(files, knownSpeed: 50)); // 900 s at 50x

        clock.Advance(20); // loading the model: no work yet, doesn't count
        Assert.Equal(TimeSpan.FromSeconds(17.4), estimate.TimeLeft([(600, 0.05), (300, 0)], knownSpeed: 50));
        clock.Advance(1);
        Assert.Equal(TimeSpan.FromSeconds(16.8), estimate.TimeLeft([(600, 0.1), (300, 0)], knownSpeed: 50));
        clock.Advance(3); // 4 s for 0.25 of 600 s: 150 s of media in 4 s; 720 s left takes 19.2 s
        Assert.Equal(TimeSpan.FromSeconds(19.2), estimate.TimeLeft([(600, 0.3), (300, 0)], knownSpeed: 50));
        Assert.Equal(TimeSpan.Zero, estimate.TimeLeft([(600, 1), (300, 1)]));
    }

    [Theory]
    [InlineData(2, "about 5 s")]
    [InlineData(41, "about 45 s")]
    [InlineData(61, "about 2 min")]
    [InlineData(4830, "about 1 h 21 min")]
    public void BatchEstimateDescribesTimeLeft(double seconds, string expected) =>
        Assert.Equal(expected, BatchEstimate.Describe(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(48.2, "48 s")]
    [InlineData(180, "3:00")]
    [InlineData(6727, "1:52:07")]
    public void BatchEstimateWritesLengths(double seconds, string expected) =>
        Assert.Equal(expected, BatchEstimate.Clock(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void RecycleBinRecordsOfBothVersionsAreRead()
    {
        var deleted = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        const string path = @"C:\Videos\talk.mp4";
        var header = BitConverter.GetBytes(0L).Concat(BitConverter.GetBytes(1234L)).Concat(BitConverter.GetBytes(deleted.ToFileTimeUtc()));
        byte[] v2 = [.. BitConverter.GetBytes(2L), .. header.Skip(8), .. BitConverter.GetBytes(path.Length + 1),
                     .. Encoding.Unicode.GetBytes(path + "\0")];
        byte[] v1 = [.. BitConverter.GetBytes(1L), .. header.Skip(8), .. Encoding.Unicode.GetBytes(path.PadRight(260, '\0'))];

        Assert.Equal((path, deleted), RecycleBinRestore.ReadInfo(v2));
        Assert.Equal((path, deleted), RecycleBinRestore.ReadInfo(v1));
        Assert.Null(RecycleBinRestore.ReadInfo([1, 2, 3]));
    }

    [Fact]
    public void ARecycledFileCanBeRestored()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var file = Path.Combine(_tmp, $"restore-me-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "hello");
        Assert.True(RecycleBin.Send(file));
        Assert.False(File.Exists(file));
        Assert.True(RecycleBinRestore.Contains(file));

        Assert.True(RecycleBinRestore.Restore(file));
        Assert.Equal("hello", File.ReadAllText(file));
        Assert.False(RecycleBinRestore.Contains(file));
        Assert.False(RecycleBinRestore.Restore(file)); // nothing left to restore
    }
}
