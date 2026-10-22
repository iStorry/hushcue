namespace Hushcue.Core.Pipeline;

/// <summary>
/// Subtitle a list of files: read the audio, transcribe (re-decoding looped passages), clean up, write the .srt and
/// optionally put it inside the video, embedding file N while file N+1 is transcribed (<see cref="JobRun"/>). SubGen's
/// run_job and transcribe_file. The model is downloaded on first use and kept loaded between runs.
/// </summary>
public static class SubtitleJob
{
    /// <summary>Releases the cached model (it is kept loaded between runs).</summary>
    public static void Unload() => JobSteps.Default.Cache.Unload();

    /// <param name="progress">overall fraction 0..1 and a status line</param>
    /// <param name="log">lines for people to read; nothing should parse them (<paramref name="onEvent"/> has the facts)</param>
    /// <param name="onFile">each file's own state as it changes; a file reaches Done (SRT written, or video embedded)
    /// or Failed at most once</param>
    /// <param name="onEvent">what the job did, as data: the device, model downloads, finished transcripts, recycled
    /// originals</param>
    public static Task<List<string>> RunAsync(IReadOnlyList<string> files, string? outDir, JobSettings settings,
                                              Action<double, string> progress, Action<string> log,
                                              Action<FileProgress>? onFile = null, Action<JobEvent>? onEvent = null,
                                              CancellationToken cancel = default) =>
        RunAsync(files, outDir, settings, progress, log, onFile, onEvent, JobSteps.Default, cancel);

    /// <summary><see cref="RunAsync(IReadOnlyList{string}, string?, JobSettings, Action{double, string}, Action{string}, Action{FileProgress}?, Action{JobEvent}?, CancellationToken)"/>
    /// with replaced steps (tests, benchmarks).</summary>
    internal static Task<List<string>> RunAsync(IReadOnlyList<string> files, string? outDir, JobSettings settings,
                                                Action<double, string> progress, Action<string> log,
                                                Action<FileProgress>? onFile, Action<JobEvent>? onEvent, JobSteps steps,
                                                CancellationToken cancel) =>
        Task.Run(() => RunJobAsync(files, outDir, settings, progress, log, onFile, onEvent ?? (_ => { }), steps, cancel), cancel);

    private static async Task<List<string>> RunJobAsync(IReadOnlyList<string> files, string? outDir, JobSettings settings,
                                                        Action<double, string> progress, Action<string> log,
                                                        Action<FileProgress>? onFile, Action<JobEvent> onEvent,
                                                        JobSteps steps, CancellationToken cancel)
    {
        var model = Presets.Get(settings.Preset).Model;
        var modelPath = steps.FindModel(model) ?? await DownloadModelAsync(model, steps, progress, log, onEvent, cancel);
        return await new JobRun(files, outDir, settings, modelPath, steps, progress, log, onFile, onEvent).RunAsync(cancel);
    }

    private static async Task<string> DownloadModelAsync(string name, JobSteps steps, Action<double, string> progress,
                                                         Action<string> log, Action<JobEvent> onEvent, CancellationToken cancel)
    {
        log($"The {name} model is not on this PC yet; downloading it (only needed once).");
        long total = 0;
        var folder = await steps.DownloadModel(name, (done, all) =>
        {
            total = all;
            progress(done / (double)all, $"Downloading the {name} model... {done * 100 / all}% of {all / 1e9:F1} GB");
            onEvent(new ModelDownloading(name, done, all));
        }, cancel);
        onEvent(new ModelDownloading(name, total, total, Finished: true));
        log($"Saved the {name} model in {folder}");
        return folder;
    }
}
