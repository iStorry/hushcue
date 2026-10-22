using Hushcue.Core.Engine;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// "Re-run this part" on the review screen: transcribe one stretch of a file again, the way loop recovery re-decodes a
/// looped passage (<see cref="FileTranscription.RecoverLoops"/>). The stretch is cut from the file's audio with
/// <see cref="FileTranscription.LoopPadSeconds"/> of context either side, decoded on its own with the loop
/// repetition penalty, and its words are moved back to the file's time; words whose middle falls in the padding
/// belong to the subtitles around the stretch and are dropped.
/// </summary>
public static class PartTranscription
{
    /// <summary>The audio decoded for [<paramref name="start"/>, <paramref name="end"/>): padded, within the file.</summary>
    public static (double Lo, double Hi) Window(double start, double end, double duration) =>
        (Math.Max(0, start - FileTranscription.LoopPadSeconds), Math.Min(duration, end + FileTranscription.LoopPadSeconds));

    /// <summary>
    /// Words decoded from a clip that starts at <paramref name="offset"/> seconds, in the file's time, keeping those
    /// whose middle is in [<paramref name="start"/>, <paramref name="end"/>).
    /// </summary>
    public static List<Word> MapWords(IEnumerable<Word> clipWords, double offset, double start, double end) =>
        [.. clipWords.Select(w => new Word(w.Text, w.Start + offset, w.End + offset))
                     .Where(w => (w.Start + w.End) / 2 is var middle && middle >= start && middle < end)];

    /// <summary>The finished cues of the words, kept inside [<paramref name="start"/>, <paramref name="end"/>].</summary>
    public static List<Cue> ToCues(IReadOnlyList<Word> words, double start, double end) =>
        [.. Cleanup.ToCues(words)
                   .Select(c => new Cue(Math.Max(c.Start, start), Math.Min(c.End, end), c.Text))
                   .Where(c => c.End - c.Start >= 0.1)];

    /// <summary>
    /// New cues for [<paramref name="start"/>, <paramref name="end"/>) seconds of <paramref name="audio"/> (the whole
    /// file's 16 kHz audio), decoded with the preset and options of <paramref name="settings"/>. Uses the model the job
    /// keeps loaded (loading it if needed, on the device it last ran on). Empty when nothing was heard.
    /// </summary>
    public static Task<List<Cue>> RunAsync(float[] audio, double start, double end, JobSettings settings, Action<string> log,
                                           CancellationToken cancel = default) =>
        Task.Run(() => Run(audio, start, end, settings, JobSteps.Default, log, cancel), cancel);

    internal static List<Cue> Run(float[] audio, double start, double end, JobSettings settings, JobSteps steps,
                                  Action<string> log, CancellationToken cancel)
    {
        var preset = Presets.Get(settings.Preset);
        var modelPath = steps.FindModel(preset.Model)
                        ?? throw new EngineException($"The {preset.Model} model is not on this PC; make subtitles for a file first to download it.");
        var duration = audio.Length / (double)Media.Audio.SampleRate;
        var (lo, hi) = Window(start, end, duration);
        var device = steps.Cache.DeviceOf(modelPath) ?? (steps.CudaDeviceCount() > 0 ? "cuda" : "cpu");
        log($"Re-running {Review.CueTime.Range(start, end)} ({Py.F0(hi - lo)} s of audio with the context around it)...");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var model = steps.Cache.Get(modelPath, device, steps.LoadModel,
                                            () => log($"Loading '{preset.Model}' on {(device == "cuda" ? "GPU" : "CPU")}..."));
                var words = FileTranscription.DecodeWords(model, FileTranscription.Slice(audio, lo, hi), settings, preset, cancel,
                                                          repetitionPenalty: FileTranscription.LoopRepetitionPenalty);
                return ToCues(MapWords(words, lo, start, end), start, end);
            }
            catch (EngineException e) when (device == "cuda" && attempt == 0 && JobRun.IsGpuError(e))
            {
                log($"GPU failed ({e.Message}). Falling back to CPU.");
                device = "cpu";
                steps.Cache.Unload();
            }
        }
    }
}
