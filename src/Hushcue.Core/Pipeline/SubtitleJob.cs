using Hushcue.Core.Engine;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// Subtitle a list of files: read the audio, transcribe, clean up, write the .srt and optionally put it inside the
/// video, embedding file N while file N+1 is transcribed (<see cref="JobRun"/>). SubGen's run_job; loop recovery is
/// still to come. The model is downloaded on first use and kept loaded between runs.
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
        Task.Run(() => RunJobAsync(files, outDir, settings, progress, log, onFileDone, cancel), cancel);

    private static async Task<List<string>> RunJobAsync(IReadOnlyList<string> files, string? outDir, JobSettings settings,
                                                        Action<double, string> progress, Action<string> log,
                                                        Action<string>? onFileDone, CancellationToken cancel)
    {
        var model = Presets.Get(settings.Preset).Model;
        var modelPath = Models.Find(model) ?? await DownloadModelAsync(model, progress, log, cancel);
        return await new JobRun(files, outDir, settings, modelPath, progress, log, onFileDone).RunAsync(cancel);
    }

    private static async Task<string> DownloadModelAsync(string name, Action<double, string> progress, Action<string> log,
                                                         CancellationToken cancel)
    {
        log($"The {name} model is not on this PC yet; downloading it (only needed once).");
        var folder = await ModelDownload.DownloadAsync(name,
            (done, total) => progress(done / (double)total,
                                      $"Downloading the {name} model... {done * 100 / total}% of {total / 1e9:F1} GB"),
            cancel);
        log($"Saved the {name} model in {folder}");
        return folder;
    }

    /// <summary>Transcribe on the GPU; if CUDA fails (missing libraries, out of memory), retry once on the CPU.</summary>
    internal static List<Cue> TranscribeWithFallback(string modelPath, Preset preset, ref string device, float[] audio,
                                                     JobSettings settings, Action<string> log, Action<double> onFraction,
                                                     Action onLoading, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var transcriber = GetTranscriber(preset.Model, modelPath, device, log, onLoading);
                return Transcribe(transcriber, audio, preset, settings, log, onFraction, cancel);
            }
            catch (EngineException e) when (device == "cuda" && attempt == 0 && IsGpuError(e))
            {
                log($"GPU failed ({e.Message}). Falling back to CPU.");
                device = "cpu";
                Unload();
            }
        }
    }

    private static Transcriber GetTranscriber(string modelName, string path, string device, Action<string> log, Action onLoading)
    {
        lock (ModelLock)
        {
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
                                        Action<string> log, Action<double> onFraction, CancellationToken cancel)
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
            Vad = preset.Vad ?? (settings.SkipSilence ? Presets.SkipSilenceVad : null),
        };
        var (segments, info) = transcriber.Transcribe(audio, options, cancel);
        if (options.Vad is not null)
            log($"Skipping silence: {info.DurationAfterVad:F0} s of {info.Duration:F0} s contain speech");
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
