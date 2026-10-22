using System.Diagnostics;
using System.Threading.Channels;
using Hushcue.Core.Engine;
using Hushcue.Core.Media;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// One run of <see cref="SubtitleJob"/>. Files are transcribed one after another; each finished transcript goes into a
/// queue that a second task works through, putting the subtitles into the video, so ffmpeg embeds file N while the
/// model transcribes file N+1. A failed transcription stops the transcribing but lets the queued embeds finish; a
/// failed embed of one video is reported at the end; anything else stops both.
/// </summary>
/// <remarks>
/// SubGen's run_job, with two deliberate differences. When a transcription fails, SubGen skips the embeds still
/// queued (their transcripts were in its temporary folder and were lost when "keep .srt" was off); Hushcue finishes
/// them first. When an embed fails with an unexpected error, SubGen stops and could report "cancelled" instead of
/// the error; Hushcue reports the error.
/// </remarks>
internal sealed class JobRun(IReadOnlyList<string> files, string? outDir, JobSettings settings, string modelPath,
                             JobSteps steps, Action<double, string> progress, Action<string> log,
                             Action<FileProgress>? onFile, Action<JobEvent> onEvent)
{
    /// <summary>A transcribed file waiting to have its subtitles put into the video.</summary>
    private sealed record EmbedWork(int Index, string Source, string Srt, bool TempSrt, string DestDir);

    private readonly Preset _preset = Presets.Get(settings.Preset);
    private readonly JobProgress _progress = new(files, progress, onFile);
    private readonly string?[] _outputs = new string?[files.Count];
    private readonly List<string> _embedFailures = [];
    // Share of each file's progress used by transcription; burning re-encodes the video and takes far longer.
    private readonly double _share = settings.Embed switch { EmbedMode.Soft => 0.85, EmbedMode.Burn => 0.3, _ => 1.0 };
    private string _device = "cpu";

    public async Task<List<string>> RunAsync(CancellationToken cancel)
    {
        _device = steps.CudaDeviceCount() > 0 ? "cuda" : "cpu";
        if (_device == "cpu")
            log("No usable NVIDIA GPU found - running on CPU, this will be slow.");
        onEvent(new DeviceSelected(_device, Reason: _device == "cpu" ? "No usable NVIDIA GPU found" : null));

        var queue = Channel.CreateUnbounded<EmbedWork>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        try
        {
            // transcription blocks its thread while the model runs, so it gets one of its own
            await Task.WhenAll(Task.Run(() => TranscribeAllAsync(queue.Writer, stop.Token), CancellationToken.None),
                               EmbedAllAsync(queue.Reader, stop));
        }
        finally
        {
            // left in the queue by a cancelled run: nothing will embed these any more
            while (queue.Reader.TryRead(out var left))
                DeleteTempSrt(left);
        }

        if (_embedFailures.Count > 0)
            throw new MediaException("Subtitles were generated, but could not be embedded into: "
                                     + string.Join(", ", _embedFailures) + ". See the log for details.");
        progress(1.0, "Done");
        return [.. _outputs.OfType<string>()];
    }

    private async Task TranscribeAllAsync(ChannelWriter<EmbedWork> queue, CancellationToken cancel)
    {
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                if (await TranscribeOrFailAsync(i, cancel) is { } work && !queue.TryWrite(work))
                    throw new InvalidOperationException("The embed queue was closed while files were still being transcribed.");
            }
        }
        finally
        {
            queue.Complete();
        }
    }

    private async Task<EmbedWork?> TranscribeOrFailAsync(int index, CancellationToken cancel)
    {
        try
        {
            return await TranscribeAsync(index, cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _progress.Failed(JobStage.Transcribing, index, e.Message);
            throw;
        }
    }

    /// <returns>the work left for the embed stage, or null when the file is finished</returns>
    private async Task<EmbedWork?> TranscribeAsync(int index, CancellationToken cancel)
    {
        var src = files[index];
        void Report(double fraction, string text, FileStage stage, RateReading? rate = null) =>
            _progress.Working(JobStage.Transcribing, index, _share * fraction, text, stage, rate);

        log($"--- {Path.GetFileName(src)}");
        Report(0, "Extracting audio...", FileStage.ReadingAudio);
        var audio = await steps.ReadAudio(src, settings.Enhance || _preset.Enhance, cancel);
        var seconds = audio.Length / (double)Audio.SampleRate;
        var rate = new TranscribeRate(seconds, TimeProvider.System);
        var (cues, took) = TranscribeWithFallback(audio, Report, f =>
        {
            var reading = rate.Measure(f);
            Report(0.08 + 0.9 * f, reading.Describe(), FileStage.Transcribing, reading);
        }, words => onEvent(new CuesDecoded(src, Cleanup.DropJunk(Cleanup.BuildCues(Cleanup.CollapseRepeats(words))))), cancel);

        Report(0.99, "Writing SRT...", FileStage.WritingSrt);
        var destDir = outDir ?? Path.GetDirectoryName(src) ?? "";
        Directory.CreateDirectory(destDir);
        var embedding = settings.Embed != EmbedMode.None && cues.Count > 0;
        var review = embedding && settings.ReviewBeforeEmbed;
        var tempSrt = embedding && !settings.KeepSrt && !review;
        var srt = tempSrt
            ? Path.Combine(Path.GetTempPath(), $"hushcue_{Guid.NewGuid():N}.srt") // only fed to ffmpeg, deleted after
            : FileNames.Unique(Path.Combine(destDir, Path.GetFileNameWithoutExtension(src) + ".srt"));
        await Srt.SaveAsync(srt, cues, cancel);
        if (!tempSrt)
            log($"Saved {srt} ({cues.Count} subtitles)");
        var warnings = Cleanup.CoverageWarnings(cues, seconds);
        foreach (var warning in warnings)
            log("WARNING: " + warning);
        if (settings.Embed != EmbedMode.None && cues.Count == 0)
            log("No subtitles were found, so nothing was embedded in the video.");
        onEvent(new FileTranscribed(src, cues, seconds, took, _device, tempSrt ? null : srt, warnings));

        if (review)
        {
            log($"{Path.GetFileName(src)} is ready to review; its subtitles will be added to the video after that.");
            _outputs[index] = srt;
            _progress.ReadyForReview(index, srt);
            return null;
        }
        if (embedding)
        {
            _progress.WaitingToEmbed(index, _share);
            return new EmbedWork(index, src, srt, tempSrt, destDir);
        }
        _outputs[index] = srt;
        _progress.Done(JobStage.Transcribing, index, srt);
        return null;
    }

    /// <summary>Transcribe on the GPU; if CUDA fails (missing libraries, out of memory), retry once on the CPU.</summary>
    /// <returns>the cues, and the time taken from the start of decoding</returns>
    private (List<Cue> Cues, TimeSpan Took) TranscribeWithFallback(
        float[] audio, Action<double, string, FileStage, RateReading?> report, Action<double> onFraction,
        Action<IReadOnlyList<Word>> onWords, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var model = steps.Cache.Get(modelPath, _device, steps.LoadModel, () =>
                {
                    report(0.05, "Loading model...", FileStage.LoadingModel, null);
                    log(_device == "cuda" ? $"Loading '{_preset.Model}' on GPU..." : $"Loading '{_preset.Model}' on CPU (slow)...");
                });
                report(0.08, "Transcribing...", FileStage.Transcribing, null);
                var clock = Stopwatch.StartNew();
                var cues = FileTranscription.Transcribe(model, audio, settings, _preset, onFraction, log, cancel, onWords);
                return (cues, clock.Elapsed);
            }
            catch (EngineException e) when (_device == "cuda" && attempt == 0 && IsGpuError(e))
            {
                log($"GPU failed ({e.Message}). Falling back to CPU.");
                _device = "cpu";
                onEvent(new DeviceSelected(_device, Fallback: true, Reason: e.Message));
                steps.Cache.Unload();
            }
        }
    }

    /// <summary>A CUDA failure (missing libraries, out of memory) that the CPU may not have.</summary>
    internal static bool IsGpuError(Exception e)
    {
        var message = e.Message.ToLowerInvariant();
        return new[] { "cublas", "cudnn", "cuda", "dll", "out of memory" }.Any(message.Contains);
    }

    private async Task EmbedAllAsync(ChannelReader<EmbedWork> queue, CancellationTokenSource stop)
    {
        try
        {
            await foreach (var work in queue.ReadAllAsync(stop.Token))
                await EmbedAsync(work, stop.Token);
        }
        catch
        {
            await stop.CancelAsync(); // stop transcribing files that can no longer be embedded
            throw;
        }
    }

    private async Task EmbedAsync(EmbedWork work, CancellationToken cancel)
    {
        var name = Path.GetFileName(work.Source);
        var what = settings.Embed == EmbedMode.Soft ? "Adding subtitle track" : "Burning subtitles into video";
        void Report(double fraction, string text) =>
            _progress.Working(JobStage.Embedding, work.Index, _share + (1 - _share) * fraction, text, FileStage.Embedding);
        try
        {
            Report(0, $"{what}...");
            var video = await steps.Embed(work.Source, work.Srt, outDir, settings.Embed,
                f => Report(f, $"{what} · {(int)(f * 100)}%"), cancel);
            log($"Saved {video}");
            _outputs[work.Index] = video;
            if (settings.DeleteOriginal)
                onEvent(new OriginalRecycled(work.Source, SubtitleJob.RecycleOriginal(steps, work.Source, video, log)));
            _progress.Done(JobStage.Embedding, work.Index, video);
        }
        catch (MediaException e)
        {
            log($"ERROR: could not embed subtitles into {name}: {e.Message}");
            _embedFailures.Add(name);
            _outputs[work.Index] = work.TempSrt ? KeepSrt(work) : work.Srt;
            _progress.Failed(JobStage.Embedding, work.Index, "Could not add the subtitles to the video; the .srt was kept");
        }
        finally
        {
            DeleteTempSrt(work);
        }
    }

    /// <summary>Don't lose the transcript just because embedding failed: copy the temporary .srt next to the video.</summary>
    private string KeepSrt(EmbedWork work)
    {
        var kept = FileNames.Unique(Path.Combine(work.DestDir, Path.GetFileNameWithoutExtension(work.Source) + ".srt"));
        File.Copy(work.Srt, kept);
        log($"Saved {kept} instead");
        return kept;
    }

    private static void DeleteTempSrt(EmbedWork work)
    {
        if (work.TempSrt)
            File.Delete(work.Srt);
    }
}
