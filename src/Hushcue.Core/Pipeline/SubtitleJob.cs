using Hushcue.Core.Engine;
using Hushcue.Core.Media;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// Subtitle a list of files: read the audio, transcribe, clean up, write the .srt and optionally put it inside the
/// video. A first version of SubGen's run_job; Phase 4 adds loop recovery, voice activity detection and embedding
/// file N while file N+1 is transcribed.
/// </summary>
public static class SubtitleJob
{
    private static readonly object ModelLock = new();
    private static WhisperModel? _model;
    private static Transcriber? _transcriber;

    /// <summary>Releases the cached model (it is kept loaded between runs).</summary>
    public static void Unload()
    {
        lock (ModelLock)
        {
            _model?.Dispose();
            (_model, _transcriber) = (null, null);
        }
    }

    /// <param name="progress">overall fraction 0..1 and a status line</param>
    /// <param name="onFileDone">a file is completely finished (SRT written, or video embedded); never fires twice</param>
    public static Task<List<string>> RunAsync(IReadOnlyList<string> files, string? outDir, JobSettings settings,
                                              Action<double, string> progress, Action<string> log,
                                              Action<string>? onFileDone = null, CancellationToken cancel = default) =>
        Task.Run(() => Run(files, outDir, settings, progress, log, onFileDone, cancel), cancel);

    private static async Task<List<string>> Run(IReadOnlyList<string> files, string? outDir, JobSettings settings,
                                                Action<double, string> progress, Action<string> log,
                                                Action<string>? onFileDone, CancellationToken cancel)
    {
        var preset = Presets.Get(settings.Preset);
        var enhance = settings.Enhance || preset.Enhance;
        var device = WhisperModel.CudaDeviceCount() > 0 ? "cuda" : "cpu";
        if (device == "cpu")
            log("No usable NVIDIA GPU found - running on CPU, this will be slow.");
        if (settings.SkipSilence || preset.VadThreshold is not null)
            log("Skipping silent parts is not available yet; the whole file is transcribed.");

        // Share of each file's progress used by transcription; burning re-encodes the video and takes far longer.
        var share = settings.Embed switch { EmbedMode.Soft => 0.85, EmbedMode.Burn => 0.3, _ => 1.0 };
        var n = files.Count;
        var outputs = new List<string>();
        var embedFailures = new List<string>();

        for (var i = 0; i < n; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var src = files[i];
            var name = Path.GetFileName(src);
            void Report(double fraction, string text) =>
                progress(Math.Min((i + fraction) / n, 1.0), text.Length > 0 ? $"[{i + 1}/{n}] {text}" : "");

            log($"--- {name}");
            Report(0, "Extracting audio...");
            var audio = await Audio.ReadAsync(src, enhance, cancel);

            var cues = TranscribeWithFallback(preset, ref device, audio, settings, log,
                f => Report(share * (0.08 + 0.9 * f), $"Transcribing... {(int)(f * 100)}%"),
                () => Report(share * 0.05, "Loading model..."), cancel);

            Report(share * 0.99, "Writing SRT...");
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
            if (!embedding)
            {
                outputs.Add(srt);
                onFileDone?.Invoke(src);
                continue;
            }

            var what = settings.Embed == EmbedMode.Soft ? "Adding subtitle track" : "Burning subtitles into video";
            try
            {
                Report(share, $"{what}...");
                var video = await Embed.SubtitlesAsync(src, srt, outDir, settings.Embed,
                    f => Report(share + (1 - share) * f, $"{what}... {(int)(f * 100)}%"), cancel: cancel);
                log($"Saved {video}");
                outputs.Add(video);
                if (settings.DeleteOriginal)
                {
                    log(new FileInfo(video).Length > 0 && RecycleBin.Send(src)
                        ? $"Moved the original {name} to the Recycle Bin"
                        : $"Kept the original {name} (could not move it to the Recycle Bin)");
                }
                onFileDone?.Invoke(src);
            }
            catch (MediaException e)
            {
                log($"ERROR: could not embed subtitles into {name}: {e.Message}");
                embedFailures.Add(name);
                if (tempSrt) // don't lose the transcript just because embedding failed
                {
                    var kept = FileNames.Unique(Path.Combine(destDir, Path.GetFileNameWithoutExtension(src) + ".srt"));
                    File.Copy(srt, kept);
                    log($"Saved {kept} instead");
                    outputs.Add(kept);
                }
                else
                {
                    outputs.Add(srt);
                }
            }
            finally
            {
                if (tempSrt)
                    File.Delete(srt);
            }
        }

        if (embedFailures.Count > 0)
            throw new MediaException("Subtitles were generated, but could not be embedded into: "
                                     + string.Join(", ", embedFailures) + ". See the log for details.");
        progress(1.0, "Done");
        return outputs;
    }

    /// <summary>Transcribe on the GPU; if CUDA fails (missing libraries, out of memory), retry once on the CPU.</summary>
    private static List<Cue> TranscribeWithFallback(Preset preset, ref string device, float[] audio, JobSettings settings,
                                                    Action<string> log, Action<double> onFraction, Action onLoading,
                                                    CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var transcriber = GetTranscriber(preset.Model, device, log, onLoading);
                return Transcribe(transcriber, audio, preset, settings, onFraction, cancel);
            }
            catch (EngineException e) when (device == "cuda" && attempt == 0 && IsGpuError(e))
            {
                log($"GPU failed ({e.Message}). Falling back to CPU.");
                device = "cpu";
                Unload();
            }
        }
    }

    private static Transcriber GetTranscriber(string modelName, string device, Action<string> log, Action onLoading)
    {
        lock (ModelLock)
        {
            var path = Models.Find(modelName)
                       ?? throw new EngineException($"The {modelName} model is not downloaded yet. "
                                                    + $"Put it in {Path.Combine(Models.Folder, modelName)}.");
            if (_model is not null && _transcriber is not null && _model.Path == path && _model.Device == device)
                return _transcriber;
            Unload();
            onLoading();
            log(device == "cuda" ? $"Loading '{modelName}' on GPU..." : $"Loading '{modelName}' on CPU (slow)...");
            _model = WhisperModel.Load(path, device, device == "cuda" ? "float16" : "int8");
            _transcriber = new Transcriber(_model);
            return _transcriber;
        }
    }

    private static List<Cue> Transcribe(Transcriber transcriber, float[] audio, Preset preset, JobSettings settings,
                                        Action<double> onFraction, CancellationToken cancel)
    {
        var options = new TranscribeOptions
        {
            Language = settings.Language,
            BeamSize = preset.BeamSize,
            Temperatures = [0.0f],
            ConditionOnPreviousText = false, // stops hallucination loops from propagating
            NoSpeechThreshold = 0.6,
            LogProbThreshold = -1.0,
            CompressionRatioThreshold = 2.4,
            WordTimestamps = true,
            HallucinationSilenceThreshold = 2.0,
            InitialPrompt = settings.Vocabulary.Trim().Length > 0 ? settings.Vocabulary.Trim() : null,
        };
        var (segments, info) = transcriber.Transcribe(audio, options, cancel);
        var total = Math.Max(info.Duration, 1.0);
        var words = new List<Word>();
        foreach (var segment in segments)
        {
            cancel.ThrowIfCancellationRequested();
            onFraction(Math.Min(segment.End / total, 1.0));
            if (segment.NoSpeechProb > 0.6 && segment.AvgLogprob < -1.0)
                continue;
            var segmentWords = segment.Words is { Count: > 0 }
                ? segment.Words.Select(w => new Word(w.Text, w.Start, w.End))
                : [new Word(segment.Text, segment.Start, segment.End)];
            words.AddRange(Cleanup.StripMarkers(segmentWords));
        }
        return Cleanup.ToCues(Cleanup.SquashStretched(words));
    }

    private static bool IsGpuError(Exception e)
    {
        var message = e.Message.ToLowerInvariant();
        return new[] { "cublas", "cudnn", "cuda", "dll", "out of memory" }.Any(message.Contains);
    }
}
