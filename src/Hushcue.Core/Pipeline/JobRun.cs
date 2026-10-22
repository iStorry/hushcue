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
                             Action<FileProgress>? onFile)
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
        void Report(double fraction, string text) => _progress.Working(JobStage.Transcribing, index, _share * fraction, text);

        log($"--- {Path.GetFileName(src)}");
        Report(0, "Extracting audio...");
        var audio = await steps.ReadAudio(src, settings.Enhance || _preset.Enhance, cancel);
        var rate = new TranscribeRate(audio.Length / (double)Audio.SampleRate, TimeProvider.System);
        var cues = TranscribeWithFallback(audio, Report, f => Report(0.08 + 0.9 * f, rate.Describe(f)), cancel);

        Report(0.99, "Writing SRT...");
        var destDir = outDir ?? Path.GetDirectoryName(src) ?? "";
        Directory.CreateDirectory(destDir);
        var embedding = settings.Embed != EmbedMode.None && cues.Count > 0;
        var tempSrt = embedding && !settings.KeepSrt;
        var srt = tempSrt
            ? Path.Combine(Path.GetTempPath(), $"hushcue_{Guid.NewGuid():N}.srt") // only fed to ffmpeg, deleted after
            : FileNames.Unique(Path.Combine(destDir, Path.GetFileNameWithoutExtension(src) + ".srt"));
        // CRLF, as SubGen's Python writes text files on Windows
        await File.WriteAllTextAsync(srt, Srt.Write(cues).Replace("\n", "\r\n"), cancel);
        if (!tempSrt)
            log($"Saved {srt} ({cues.Count} subtitles)");
        foreach (var warning in Cleanup.CoverageWarnings(cues, audio.Length / (double)Audio.SampleRate))
            log("WARNING: " + warning);
        if (settings.Embed != EmbedMode.None && cues.Count == 0)
            log("No subtitles were found, so nothing was embedded in the video.");

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
    private List<Cue> TranscribeWithFallback(float[] audio, Action<double, string> report, Action<double> onFraction,
                                             CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var model = steps.Cache.Get(modelPath, _device, steps.LoadModel, () =>
                {
                    report(0.05, "Loading model...");
                    log(_device == "cuda" ? $"Loading '{_preset.Model}' on GPU..." : $"Loading '{_preset.Model}' on CPU (slow)...");
                });
                report(0.08, "Transcribing...");
                return FileTranscription.Transcribe(model, audio, settings, _preset, onFraction, log, cancel);
            }
            catch (EngineException e) when (_device == "cuda" && attempt == 0 && IsGpuError(e))
            {
                log($"GPU failed ({e.Message}). Falling back to CPU.");
                _device = "cpu";
                steps.Cache.Unload();
            }
        }
    }

    private static bool IsGpuError(Exception e)
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
            _progress.Working(JobStage.Embedding, work.Index, _share + (1 - _share) * fraction, text);
        try
        {
            Report(0, $"{what}...");
            var video = await steps.Embed(work.Source, work.Srt, outDir, settings.Embed,
                f => Report(f, $"{what}... {(int)(f * 100)}%"), cancel);
            log($"Saved {video}");
            _outputs[work.Index] = video;
            if (settings.DeleteOriginal)
            {
                log(new FileInfo(video).Length > 0 && steps.Recycle(work.Source)
                    ? $"Moved the original {name} to the Recycle Bin"
                    : $"Kept the original {name} (could not move it to the Recycle Bin)");
            }
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
