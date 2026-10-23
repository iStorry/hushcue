using System.Diagnostics;
using Hushcue.Core.Engine;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

/// <summary>
/// The subtitle job end to end with a fake Whisper model but real ffmpeg: embedding modes, progress, failure handling,
/// the embed overlap.
/// </summary>
public sealed class JobTests : IDisposable
{
    private static readonly Word[] Hello = [new(" hello", 1.0, 1.4), new(" there", 1.5, 2.0)];
    private readonly string _dir = Directory.CreateTempSubdirectory("hushcue_job_").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string MakeVideo(string name, int seconds)
    {
        var path = Path.Combine(_dir, name);
        var info = new ProcessStartInfo(Tools.Ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-y", "-v", "error", "-f", "lavfi", "-i", $"testsrc2=s=320x180:d={seconds}:r=25", "-f", "lavfi",
                                    "-i", $"sine=d={seconds}", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors);
        return path;
    }

    private string Clip() => MakeVideo("clip.mp4", 4);

    private List<string> TwoVideos() => [MakeVideo("a.mp4", 2), MakeVideo("b.mp4", 2)];

    private List<string> Names() => [.. Directory.EnumerateFiles(_dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    /// <summary>Real audio extraction and embedding (x264, so the test does not depend on the GPU), the given model.</summary>
    private static JobSteps Steps(ITranscriber model, Func<string, string, string?, EmbedMode, Action<double>, CancellationToken, Task<string>>? embed = null,
                                  Func<string, bool>? recycle = null) =>
        new()
        {
            CudaDeviceCount = () => 0,
            FindModel = _ => "fake-model",
            LoadModel = (_, _) => model,
            Embed = embed ?? RealEmbed,
            Recycle = recycle ?? (_ => throw new InvalidOperationException("recycled without asking")),
        };

    private static Task<string> RealEmbed(string video, string srt, string? outDir, EmbedMode mode, Action<double> onFraction,
                                          CancellationToken cancel) =>
        Embed.SubtitlesAsync(video, srt, outDir, mode, onFraction, Embed.X264Args, cancel);

    private static Task<string> FailingEmbed(string video, string srt, string? outDir, EmbedMode mode, Action<double> onFraction,
                                             CancellationToken cancel) =>
        throw new MediaException("disk full");

    private sealed record Run(List<string> Outputs, List<(double Fraction, string Text)> Seen, List<FileProgress> Files,
                              List<string> Log, List<JobEvent> Events);

    private static async Task<Run> RunAsync(IReadOnlyList<string> files, JobSettings settings, JobSteps steps,
                                            CancellationToken cancel = default)
    {
        var seen = new List<(double, string)>();
        var fileStates = new List<FileProgress>();
        var log = new List<string>();
        var events = new List<JobEvent>();
        var outputs = await SubtitleJob.RunAsync(files, null, settings, (f, t) => seen.Add((f, t)),
                                                 line => { lock (log) log.Add(line); }, fileStates.Add,
                                                 e => { lock (events) events.Add(e); }, steps, cancel);
        return new Run(outputs, seen, fileStates, log, events);
    }

    private static List<string> Done(IEnumerable<FileProgress> files) =>
        [.. files.Where(f => f.State == FileState.Done).Select(f => f.Path)];

    private static void AssertMonotonic(List<(double Fraction, string Text)> seen)
    {
        var fractions = seen.Select(s => s.Fraction).ToList();
        Assert.Equal(1.0, fractions[^1]);
        Assert.All(fractions.Zip(fractions.Skip(1)), pair => Assert.True(pair.First <= pair.Second + 1e-9, $"{pair.First} > {pair.Second}"));
    }

    [FfmpegFact]
    public async Task DefaultWritesOnlyTheSrt()
    {
        var clip = Clip();
        var run = await RunAsync([clip], new JobSettings(), Steps(new FakeTranscriber(Hello, duration: 4)));
        Assert.Equal(["clip.srt"], run.Outputs.Select(Path.GetFileName));
        Assert.Equal(["clip.mp4", "clip.srt"], Names());
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,000\r\nhello there\r\n", File.ReadAllText(run.Outputs[0]));
    }

    [FfmpegFact]
    public async Task BurnKeepsTheSrtByDefaultAndReportsTheVideo()
    {
        var clip = Clip();
        var run = await RunAsync([clip], new JobSettings { Embed = EmbedMode.Burn }, Steps(new FakeTranscriber(Hello, duration: 4)));
        Assert.Equal(["clip.subbed.mp4"], run.Outputs.Select(Path.GetFileName)); // "clip.mp4" is the source, so .subbed
        Assert.Equal(["clip.mp4", "clip.srt", "clip.subbed.mp4"], Names());
        AssertMonotonic(run.Seen);
        var firstBurn = run.Seen.FindIndex(s => s.Text.Contains("Burning subtitles"));
        Assert.All(run.Seen[..firstBurn], s => Assert.True(s.Fraction <= 0.3 + 1e-9)); // transcription uses the first 30%
        Assert.All(run.Seen[firstBurn..], s => Assert.DoesNotContain("Transcribing", s.Text));
    }

    [FfmpegFact]
    public async Task BurnWithoutKeepingTheSrtLeavesJustTheTwoVideos()
    {
        await RunAsync([Clip()], new JobSettings { Embed = EmbedMode.Burn, KeepSrt = false }, Steps(new FakeTranscriber(Hello, duration: 4)));
        Assert.Equal(["clip.mp4", "clip.subbed.mp4"], Names());
    }

    [FfmpegFact]
    public async Task SoftModeMakesAnMkv()
    {
        await RunAsync([Clip()], new JobSettings { Embed = EmbedMode.Soft, KeepSrt = false }, Steps(new FakeTranscriber(Hello, duration: 4)));
        Assert.Equal(["clip.mkv", "clip.mp4"], Names());
    }

    [FfmpegFact]
    public async Task NoSpeechMeansNothingIsEmbedded()
    {
        await RunAsync([Clip()], new JobSettings { Embed = EmbedMode.Burn }, Steps(new FakeTranscriber([], duration: 4)));
        Assert.Equal(["clip.mp4", "clip.srt"], Names());
    }

    [FfmpegFact]
    public async Task EmbedFailureStillSavesTheTranscriptAndReportsIt()
    {
        var clip = Clip();
        var error = await Assert.ThrowsAsync<MediaException>(() => RunAsync(
            [clip], new JobSettings { Embed = EmbedMode.Burn, KeepSrt = false }, Steps(new FakeTranscriber(Hello, duration: 4), FailingEmbed)));
        Assert.Contains("could not be embedded into: clip.mp4", error.Message);
        Assert.Equal(["clip.mp4", "clip.srt"], Names()); // transcript rescued from the temporary file
    }

    // --- queue behaviour

    [FfmpegFact]
    public async Task EachFinishedFileIsReportedOnce()
    {
        var videos = TwoVideos();
        var run = await RunAsync(videos, new JobSettings(), Steps(new FakeTranscriber(Hello)));
        Assert.Equal(videos, Done(run.Files));
    }

    [FfmpegFact]
    public async Task EmbeddingOverlapsWithTranscribingTheNextFile()
    {
        // File 2 must be transcribed while file 1 is still being embedded, not after.
        var videos = TwoVideos();
        using var secondStarted = new ManualResetEventSlim();
        var calls = 0;
        var counting = new FakeTranscriber(Hello)
        {
            OnTranscribe = _ =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                    secondStarted.Set();
            },
        };
        async Task<string> SlowEmbed(string video, string srt, string? outDir, EmbedMode mode, Action<double> onFraction,
                                     CancellationToken cancel)
        {
            // Only finishes once file 2's transcription has begun; would time out if the stages ran one after another.
            Assert.True(await Task.Run(() => secondStarted.Wait(TimeSpan.FromSeconds(20))),
                        "file 2 was not transcribed while file 1 was embedding");
            return await RealEmbed(video, srt, outDir, mode, onFraction, cancel);
        }

        var run = await RunAsync(videos, new JobSettings { Embed = EmbedMode.Soft }, Steps(counting, SlowEmbed));
        Assert.Equal(["a.mkv", "b.mkv"], run.Outputs.Select(Path.GetFileName).Order());
        Assert.Equal(videos, Done(run.Files).Order());
    }

    [FfmpegFact]
    public async Task DeleteOriginalRemovesTheSourceOnlyAfterTheNewFileExists()
    {
        var videos = TwoVideos();
        var seen = new List<(string, List<string>)>();
        bool FakeRecycle(string path)
        {
            seen.Add((Path.GetFileName(path), Names()));
            File.Delete(path);
            return true;
        }

        await RunAsync(videos[..1], new JobSettings { Embed = EmbedMode.Soft, KeepSrt = false, DeleteOriginal = true },
                       Steps(new FakeTranscriber(Hello), recycle: FakeRecycle));
        var (name, folder) = Assert.Single(seen);
        Assert.Equal("a.mp4", name);
        Assert.Equal(["a.mkv", "a.mp4", "b.mp4"], folder); // the new file was already there
        Assert.Equal(["a.mkv", "b.mp4"], Names());
    }

    [FfmpegFact]
    public async Task OriginalIsKeptWhenEmbeddingFailsOrOptionIsOff()
    {
        var videos = TwoVideos();
        var recycled = new List<string>();
        bool Record(string path)
        {
            recycled.Add(path);
            return true;
        }

        await RunAsync(videos[..1], new JobSettings { Embed = EmbedMode.Soft }, Steps(new FakeTranscriber(Hello), recycle: Record));
        Assert.Empty(recycled); // option off: never touched

        await Assert.ThrowsAsync<MediaException>(() => RunAsync(
            videos[1..], new JobSettings { Embed = EmbedMode.Soft, DeleteOriginal = true },
            Steps(new FakeTranscriber(Hello), FailingEmbed, Record)));
        Assert.Empty(recycled); // failed: original kept
        Assert.True(File.Exists(videos[1]));
    }

    [FfmpegFact]
    public async Task FailedEmbeddingIsNotReportedAsDone()
    {
        var videos = TwoVideos();
        var files = new List<FileProgress>();
        await Assert.ThrowsAsync<MediaException>(() => SubtitleJob.RunAsync(
            videos, null, new JobSettings { Embed = EmbedMode.Soft }, (_, _) => { }, _ => { }, files.Add, null,
            Steps(new FakeTranscriber(Hello), FailingEmbed), default));
        Assert.Empty(Done(files)); // stays in the queue to retry
        Assert.Equal(2, files.Count(f => f.State == FileState.Failed));
    }

    [WindowsFact]
    public void RecycleMovesARealFileOutOfTheFolder()
    {
        var file = Path.Combine(_dir, "gone.txt");
        File.WriteAllText(file, "x");
        Assert.True(RecycleBin.Send(file));
        Assert.False(File.Exists(file));
        Assert.False(RecycleBin.Send(Path.Combine(_dir, "missing.txt")));
    }

    // --- The dropped "Best" preset

    [FfmpegFact]
    public async Task ProgressRisesSteadilyOverSeveralFiles()
    {
        // each file's audio is read once, in order, and the overall
        // progress never goes back
        var videos = TwoVideos();
        var read = new List<string>();
        var steps = Steps(new FakeTranscriber(Hello));
        steps = new JobSteps
        {
            CudaDeviceCount = steps.CudaDeviceCount, FindModel = steps.FindModel, LoadModel = steps.LoadModel,
            ReadAudio = (path, enhance, cancel) =>
            {
                read.Add(Path.GetFileName(path));
                return Audio.ReadAsync(path, enhance, cancel);
            },
        };
        var run = await RunAsync(videos, new JobSettings(), steps);
        Assert.Equal(["a.mp4", "b.mp4"], read);
        AssertMonotonic(run.Seen);
    }

    [FfmpegFact]
    public async Task TheDroppedBestPresetRunsAsBalancedOnThePlainAudio()
    {
        // old settings may still say Best
        var videos = TwoVideos();
        var model = new FakeTranscriber(Hello);
        var wanted = new List<string>();
        var steps = Steps(model);
        steps = new JobSteps
        {
            CudaDeviceCount = steps.CudaDeviceCount, LoadModel = steps.LoadModel,
            FindModel = name => { wanted.Add(name); return "fake-model"; },
        };
        var run = await RunAsync(videos[..1], new JobSettings { Preset = "Best" }, steps);
        Assert.Equal(["a.srt"], run.Outputs.Select(Path.GetFileName));
        Assert.Equal(["large-v3-turbo"], wanted);
        Assert.Equal(5, model.Calls[0].Options.BeamSize);
        Assert.Null(model.Calls[0].Options.Vad);
    }

    // --- Hushcue's own behaviour

    [FfmpegFact]
    public async Task CancelStopsTheJobAndLeavesNoTemporaryFiles()
    {
        var videos = TwoVideos();
        using var cancel = new CancellationTokenSource();
        var model = new FakeTranscriber(Hello) { OnTranscribe = _ => cancel.Cancel() };
        var files = new List<FileProgress>();
        var temp = Directory.GetFiles(Path.GetTempPath(), "hushcue_*.srt").Length;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SubtitleJob.RunAsync(
            videos, null, new JobSettings { Embed = EmbedMode.Soft, KeepSrt = false }, (_, _) => { }, _ => { }, files.Add, null,
            Steps(model), cancel.Token));
        Assert.Empty(Done(files));
        Assert.Equal(["a.mp4", "b.mp4"], Names());
        Assert.Single(model.Calls);
        Assert.True(Directory.GetFiles(Path.GetTempPath(), "hushcue_*.srt").Length <= temp);
    }

    [FfmpegFact]
    public async Task AFailedTranscriptionStillLetsTheQueuedEmbedFinish()
    {
        // the queued embed still finishes
        var videos = TwoVideos();
        using var failed = new ManualResetEventSlim();
        var calls = 0;
        var model = new FakeTranscriber(Hello)
        {
            OnTranscribe = _ =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    failed.Set();
                    throw new EngineException("the second file broke the decoder");
                }
            },
        };
        async Task<string> EmbedAfterTheFailure(string video, string srt, string? outDir, EmbedMode mode,
                                                Action<double> onFraction, CancellationToken cancel)
        {
            Assert.True(await Task.Run(() => failed.Wait(TimeSpan.FromSeconds(20))));
            return await RealEmbed(video, srt, outDir, mode, onFraction, cancel);
        }

        var files = new List<FileProgress>();
        var error = await Assert.ThrowsAsync<EngineException>(() => SubtitleJob.RunAsync(
            videos, null, new JobSettings { Embed = EmbedMode.Soft, KeepSrt = false }, (_, _) => { }, _ => { }, files.Add, null,
            Steps(model, EmbedAfterTheFailure), default));
        Assert.Contains("broke the decoder", error.Message);
        Assert.Equal(["a.mkv", "a.mp4", "b.mp4"], Names());
        Assert.Equal([videos[0]], Done(files));
        Assert.Equal(videos[1], Assert.Single(files, f => f.State == FileState.Failed).Path);
    }

    [FfmpegFact]
    public async Task GpuFailureFallsBackToTheCpu()
    {
        var clip = Clip();
        var loads = new List<string>();
        var broken = new FakeTranscriber(Hello) { OnTranscribe = _ => throw new EngineException("CUDA failed with error out of memory") };
        var working = new FakeTranscriber(Hello);
        var steps = new JobSteps
        {
            CudaDeviceCount = () => 1,
            FindModel = _ => "fake-model",
            LoadModel = (_, device) =>
            {
                loads.Add(device);
                return device == "cuda" ? broken : working;
            },
        };
        var run = await RunAsync([clip], new JobSettings(), steps);
        Assert.Equal(["cuda", "cpu"], loads);
        Assert.Equal(["clip.srt"], run.Outputs.Select(Path.GetFileName));
        Assert.Contains(run.Log, line => line.StartsWith("GPU failed (CUDA failed", StringComparison.Ordinal));
        // the same facts as data, for the app's badge and speed hint
        var devices = run.Events.OfType<DeviceSelected>().ToList();
        Assert.Equal(["cuda", "cpu"], devices.Select(d => d.Device));
        Assert.False(devices[0].Fallback);
        Assert.True(devices[1].Fallback);
        Assert.Contains("out of memory", devices[1].Reason);
        Assert.Equal("cpu", Assert.Single(run.Events.OfType<FileTranscribed>()).Device);
    }

    // --- review before embedding

    [FfmpegFact]
    public async Task ReviewBeforeEmbeddingStopsAtTheSrt()
    {
        var clip = Clip();
        var run = await RunAsync([clip], new JobSettings { Embed = EmbedMode.Soft, KeepSrt = false, DeleteOriginal = true, ReviewBeforeEmbed = true },
                                 Steps(new FakeTranscriber(Hello, duration: 4), FailingEmbed));
        Assert.Equal(["clip.mp4", "clip.srt"], Names()); // the .srt is kept for the review, the video not touched yet
        Assert.Equal(FileProgress.ReadyForReview(clip, Path.ChangeExtension(clip, ".srt")), run.Files[^1]);
        Assert.Equal(run.Outputs, [Path.ChangeExtension(clip, ".srt")]);
        Assert.Equal(Path.ChangeExtension(clip, ".srt"), Assert.Single(run.Events.OfType<FileTranscribed>()).Srt);
        AssertMonotonic(run.Seen);
    }

    [FfmpegFact]
    public async Task ReviewedSubtitlesAreEmbeddedAfterwards()
    {
        var clip = Clip();
        var recycled = new List<string>();
        var steps = Steps(new FakeTranscriber(Hello), recycle: path =>
        {
            recycled.Add(path);
            return true;
        });
        var fractions = new List<double>();
        var result = await SubtitleJob.EmbedAsync(clip, [new Cue(0.5, 2.0, "edited by hand")], null, EmbedMode.Soft, deleteOriginal: true,
                                                  fractions.Add, _ => { }, steps, default);
        Assert.Equal(new EmbedResult(Path.ChangeExtension(clip, ".mkv"), Recycled: true), result);
        Assert.Equal([clip], recycled);
        Assert.NotEmpty(fractions);
        var (exitCode, srt, _) = await Tools.RunAsync([Tools.Ffmpeg, "-v", "error", "-i", result.Video, "-map", "0:s:0", "-f", "srt", "-"]);
        Assert.Equal(0, exitCode);
        Assert.Equal(["1", "00:00:00,500 --> 00:00:02,000", "edited by hand"], srt.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        await Assert.ThrowsAsync<MediaException>(() => SubtitleJob.EmbedAsync(clip, [], null, EmbedMode.Soft, false, _ => { }, _ => { }, steps, default));
    }

    [FfmpegFact]
    public async Task CuesAreReportedAsTheyAreDecoded()
    {
        var clip = Clip();
        var run = await RunAsync([clip], new JobSettings(), Steps(new FakeTranscriber(Hello, duration: 4)));
        var live = Assert.Single(run.Events.OfType<CuesDecoded>()); // the fake decodes one segment
        Assert.Equal(clip, live.Path);
        Assert.Equal([new Cue(1.0, 2.0, "hello there")], live.Cues);
        Assert.True(run.Events.IndexOf(live) < run.Events.FindIndex(e => e is FileTranscribed));
    }

    // --- what the job reports as data (JobEvent, FileProgress.Stage), so front ends never read the log

    [FfmpegFact]
    public async Task EachFileGoesThroughItsStagesInOrder()
    {
        var clip = Clip();
        var run = await RunAsync([clip], new JobSettings { Embed = EmbedMode.Soft }, Steps(new FakeTranscriber(Hello, duration: 4)));
        var stages = run.Files.Select(f => f.Stage).Distinct().ToList();
        Assert.Equal([FileStage.ReadingAudio, FileStage.LoadingModel, FileStage.Transcribing, FileStage.WritingSrt,
                      FileStage.WaitingToEmbed, FileStage.Embedding, FileStage.None], stages);
        Assert.True(run.Files[0].IsStarting);
        Assert.Equal(FileState.Done, run.Files[^1].State);
        Assert.Equal(Path.ChangeExtension(clip, ".mkv"), run.Files[^1].Output);
    }

    [FfmpegFact]
    public async Task AFinishedTranscriptIsReportedWithItsCuesLengthAndSrt()
    {
        var clip = Clip();
        var run = await RunAsync([clip], new JobSettings(), Steps(new FakeTranscriber(Hello, duration: 4)));
        Assert.Equal(new DeviceSelected("cpu", Reason: "No usable NVIDIA GPU found"), run.Events[0]);
        var done = Assert.Single(run.Events.OfType<FileTranscribed>());
        Assert.Equal(clip, done.Path);
        Assert.Equal([new Cue(1.0, 2.0, "hello there")], done.Cues);
        Assert.Equal(4.0, done.AudioSeconds, 1);
        Assert.True(done.Took > TimeSpan.Zero && done.Speed > 0);
        Assert.Equal(run.Outputs[0], done.Srt);
        Assert.Empty(done.Warnings);
    }

    [FfmpegFact]
    public async Task CoverageWarningsAndTemporarySrtsAreReported()
    {
        var run = await RunAsync([Clip()], new JobSettings { Embed = EmbedMode.Soft, KeepSrt = false },
                                 Steps(new FakeTranscriber([], duration: 4)));
        var done = Assert.Single(run.Events.OfType<FileTranscribed>());
        Assert.Empty(done.Cues);
        Assert.StartsWith("No speech was found", Assert.Single(done.Warnings));

        run = await RunAsync([Clip()], new JobSettings { Embed = EmbedMode.Soft, KeepSrt = false },
                             Steps(new FakeTranscriber(Hello, duration: 4)));
        Assert.Null(Assert.Single(run.Events.OfType<FileTranscribed>()).Srt); // only a temporary file, fed to ffmpeg
    }

    [FfmpegFact]
    public async Task RecycledOriginalsAreReported()
    {
        var videos = TwoVideos();
        var run = await RunAsync(videos, new JobSettings { Embed = EmbedMode.Soft, DeleteOriginal = true },
                                 Steps(new FakeTranscriber(Hello), recycle: path => path == videos[0]));
        Assert.Equal([new OriginalRecycled(videos[0], true), new OriginalRecycled(videos[1], false)],
                     run.Events.OfType<OriginalRecycled>().OrderBy(e => e.Path));
    }

    [FfmpegFact]
    public async Task TheModelIsDownloadedWhenMissingAndLoadedOnce()
    {
        var videos = TwoVideos();
        var downloads = new List<string>();
        var loads = 0;
        var steps = new JobSteps
        {
            CudaDeviceCount = () => 0,
            FindModel = _ => null,
            DownloadModel = (name, progress, _) =>
            {
                downloads.Add(name);
                progress(50, 100);
                progress(100, 100);
                return Task.FromResult("downloaded-model");
            },
            LoadModel = (path, _) =>
            {
                Assert.Equal("downloaded-model", path);
                loads++;
                return new FakeTranscriber(Hello);
            },
        };
        var run = await RunAsync(videos, new JobSettings(), steps);
        Assert.Equal(["large-v3-turbo"], downloads);
        Assert.Equal(1, loads);
        Assert.Contains(run.Seen, s => s.Text.StartsWith("Downloading the large-v3-turbo model... 50%", StringComparison.Ordinal));
        Assert.Equal([new ModelDownloading("large-v3-turbo", 50, 100), new ModelDownloading("large-v3-turbo", 100, 100),
                      new ModelDownloading("large-v3-turbo", 100, 100, Finished: true)],
                     run.Events.OfType<ModelDownloading>());
        Assert.Equal(2, run.Outputs.Count);
    }
}
