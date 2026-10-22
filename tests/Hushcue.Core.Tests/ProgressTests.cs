using Hushcue.Core.Pipeline;

namespace Hushcue.Core.Tests;

public class ProgressTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    [Fact]
    public void JobProgressReportsTheOverallValueAndEachFile()
    {
        var overall = new List<(double Fraction, string Text)>();
        var perFile = new List<FileProgress>();
        var progress = new JobProgress(["a.mp4", "b.mp4"], (f, t) => overall.Add((f, t)), perFile.Add);

        progress.Working(JobStage.Transcribing, 0, 0.5, "Transcribing...");
        Assert.Equal((0.25, "[1/2] Transcribing..."), overall[^1]);
        Assert.Equal(FileProgress.Working("a.mp4", 0.5, "Transcribing..."), perFile[^1]);

        progress.WaitingToEmbed(0, 0.8);
        progress.Working(JobStage.Transcribing, 1, 0.2, "Extracting audio...");
        progress.Working(JobStage.Embedding, 0, 0.9, "Adding subtitle track...");
        Assert.Equal((0.55, "[2/2] Extracting audio...  ·  [1/2] Adding subtitle track..."), overall[^1]);
        Assert.Equal(FileState.Working, perFile[1].State);

        progress.Done(JobStage.Embedding, 0, @"out\a.mkv");
        progress.Failed(JobStage.Transcribing, 1, "No audio stream");
        Assert.Equal((1.0, ""), overall[^1]);
        Assert.Equal(FileProgress.Done("a.mp4", @"out\a.mkv"), perFile[^2]);
        Assert.Equal(FileProgress.Failed("b.mp4", "No audio stream"), perFile[^1]);
    }

    [Fact]
    public void TranscribeRateWaitsForAMeasurementThenShowsSpeedAndTimeLeft()
    {
        var clock = new ManualClock();
        var rate = new TranscribeRate(audioSeconds: 600, clock);

        clock.Advance(30); // loading the model before the first report doesn't count
        Assert.Equal("Transcribing... 10%", rate.Describe(0.1));
        clock.Advance(1);
        Assert.Equal("Transcribing... 15%", rate.Describe(0.15));
        clock.Advance(3); // 4 s for 0.4 of 600 s of audio: 60x, 300 s of audio left takes 5 s
        Assert.Equal("Transcribing... 60× real time, about 5 s left", rate.Describe(0.5));
    }

    [Theory]
    [InlineData(4.2, "about 5 s left")]
    [InlineData(59.5, "about 60 s left")]
    [InlineData(60, "about 1 min left")]
    [InlineData(61, "about 2 min left")]
    public void TimeLeftRoundsUp(double seconds, string expected) =>
        Assert.Equal(expected, TranscribeRate.TimeLeft(TimeSpan.FromSeconds(seconds)));
}
