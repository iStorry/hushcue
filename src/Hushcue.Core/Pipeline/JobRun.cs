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
internal sealed class JobRun(IReadOnlyList<string> files, string? outDir, JobSettings settings, string modelPath,
                             Action<double, string> progress, Action<string> log, Action<string>? onFileDone)
{
    /// <summary>A transcribed file waiting to have its subtitles put into the video.</summary>
    private sealed record EmbedWork(int Index, string Source, string Srt, bool TempSrt, string DestDir);

    private readonly Preset _preset = Presets.Get(settings.Preset);
    private readonly JobProgress _progress = new(files.Count, progress);
    private readonly string?[] _outputs = new string?[files.Count];
    private readonly List<string> _embedFailures = [];
    // Share of each file's progress used by transcription; burning re-encodes the video and takes far longer.
    private readonly double _share = settings.Embed switch { EmbedMode.Soft => 0.85, EmbedMode.Burn => 0.3, _ => 1.0 };
    private string _device = "cpu";

    public async Task<List<string>> RunAsync(CancellationToken cancel)
    {
        _device = WhisperModel.CudaDeviceCount() > 0 ? "cuda" : "cpu";
        if (_device == "cpu")
            log("No usable NVIDIA GPU found - running on CPU, this will be slow.");

        var queue = Channel.CreateUnbounded<EmbedWork>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        try
        {
            await Task.WhenAll(TranscribeAllAsync(queue.Writer, stop.Token), EmbedAllAsync(queue.Reader, stop));
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
                if (await TranscribeAsync(i, cancel) is { } work && !queue.TryWrite(work))
                    throw new InvalidOperationException("The embed queue was closed while files were still being transcribed.");
            }
        }
        finally
        {
            queue.Complete();
        }
    }

    /// <returns>the work left for the embed stage, or null when the file is finished</returns>
    private async Task<EmbedWork?> TranscribeAsync(int index, CancellationToken cancel)
    {
        var src = files[index];
        void Report(double fraction, string text) => _progress.Report(JobStage.Transcribing, index, _share * fraction, text);

        log($"--- {Path.GetFileName(src)}");
        Report(0, "Extracting audio...");
        var audio = await Audio.ReadAsync(src, settings.Enhance || _preset.Enhance, cancel);
        var cues = SubtitleJob.TranscribeWithFallback(modelPath, _preset, ref _device, audio, settings, log,
            f => Report(0.08 + 0.9 * f, $"Transcribing... {(int)(f * 100)}%"),
            () => Report(0.05, "Loading model..."), cancel);

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
            _progress.Report(JobStage.Transcribing, index, _share, "");
            return new EmbedWork(index, src, srt, tempSrt, destDir);
        }
        _outputs[index] = srt;
        _progress.Report(JobStage.Transcribing, index, 1, "");
        onFileDone?.Invoke(src);
        return null;
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
            _progress.Report(JobStage.Embedding, work.Index, _share + (1 - _share) * fraction, text);
        try
        {
            Report(0, $"{what}...");
            var video = await Embed.SubtitlesAsync(work.Source, work.Srt, outDir, settings.Embed,
                f => Report(f, $"{what}... {(int)(f * 100)}%"), cancel: cancel);
            log($"Saved {video}");
            _outputs[work.Index] = video;
            if (settings.DeleteOriginal)
            {
                log(new FileInfo(video).Length > 0 && RecycleBin.Send(work.Source)
                    ? $"Moved the original {name} to the Recycle Bin"
                    : $"Kept the original {name} (could not move it to the Recycle Bin)");
            }
            onFileDone?.Invoke(work.Source);
        }
        catch (MediaException e)
        {
            log($"ERROR: could not embed subtitles into {name}: {e.Message}");
            _embedFailures.Add(name);
            _outputs[work.Index] = work.TempSrt ? KeepSrt(work) : work.Srt;
        }
        finally
        {
            DeleteTempSrt(work);
        }
        Report(1, "");
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
