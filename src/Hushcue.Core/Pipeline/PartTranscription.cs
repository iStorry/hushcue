using Hushcue.Core.Engine;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// "Re-run this part" on the review screen: transcribe one stretch of a file again, the way loop recovery re-decodes a
/// looped passage (<see cref="FileTranscription.RecoverLoops"/>). The stretch is cut from the file's audio with
/// <see cref="FileTranscription.LoopPadSeconds"/> of context either side, decoded on its own with the loop
/// repetition penalty, and its words are moved back to the file's time; words whose middle falls in the padding
/// belong to the subtitles around the stretch and are dropped. "Try another quality" decodes the same way with another
/// preset, whose model may hear the stretch better (large-v3 for Thorough, large-v3-turbo for the others).
/// </summary>
public static class PartTranscription
{
    /// <summary>How far a re-run reaches into the silence either side of the subtitles it replaces.</summary>
    public const double EdgeSeconds = 1.0;

    /// <summary>
    /// The stretch to re-run for subtitles from <paramref name="start"/> to <paramref name="end"/>: up to
    /// <see cref="EdgeSeconds"/> further either side, but not past the end of the subtitle before
    /// (<paramref name="before"/>) or the start of the one after (<paramref name="after"/>). Another model may time a
    /// word a little earlier or later than the subtitle did; without the room it would be dropped.
    /// </summary>
    public static (double Start, double End) Widen(double start, double end, double before, double after) =>
        (Math.Min(start, Math.Max(before, start - EdgeSeconds)), Math.Max(end, Math.Min(after, end + EdgeSeconds)));

    /// <summary>The audio decoded for [<paramref name="start"/>, <paramref name="end"/>): padded, within the file.</summary>
    public static (double Lo, double Hi) Window(double start, double end, double duration) =>
        (Math.Max(0, start - FileTranscription.LoopPadSeconds), Math.Min(duration, end + FileTranscription.LoopPadSeconds));

    /// <summary>
    /// Words decoded from a clip that starts at <paramref name="offset"/> seconds, in the file's time, keeping those
    /// whose middle is in [<paramref name="start"/>, <paramref name="end"/>).
    /// </summary>
    public static List<Word> MapWords(IEnumerable<Word> clipWords, double offset, double start, double end) =>
        [.. clipWords.Select(w => w with { Start = w.Start + offset, End = w.End + offset })
                     .Where(w => (w.Start + w.End) / 2 is var middle && middle >= start && middle < end)];

    /// <summary>The finished cues of the words, kept inside [<paramref name="start"/>, <paramref name="end"/>].</summary>
    public static List<Cue> ToCues(IReadOnlyList<Word> words, double start, double end) =>
        [.. Cleanup.ToCues(words)
                   .Select(c => new Cue(Math.Max(c.Start, start), Math.Min(c.End, end), c.Text))
                   .Where(c => c.End - c.Start >= 0.1)];

    /// <summary>
    /// New cues for [<paramref name="start"/>, <paramref name="end"/>) seconds of <paramref name="audio"/>, decoded with
    /// the preset and options of <paramref name="settings"/>: the job's own to re-run a part, another preset to compare
    /// what its model hears. Uses the model the job keeps loaded when it is the preset's (loading it if needed, on the
    /// device it last ran on). Empty when nothing was heard.
    /// </summary>
    public static Task<List<Cue>> RunAsync(PartAudio audio, double start, double end, JobSettings settings, Action<string> log,
                                           CancellationToken cancel = default) =>
        Task.Run(() => RunAsync(audio, start, end, settings, JobSteps.Default, log, cancel), cancel);

    /// <summary>Whether the model of the preset called <paramref name="preset"/> is on this PC, so it can re-run a part.</summary>
    public static bool HasModel(string preset) => Models.Find(Presets.Get(preset).Model) is not null;

    /// <summary>Whether a preset hears the audio with ffmpeg's clean-up (<see cref="Media.Audio.EnhanceFilter"/>).</summary>
    public static bool Enhances(JobSettings settings) => settings.Enhance || Presets.Get(settings.Preset).Enhance;

    internal static async Task<List<Cue>> RunAsync(PartAudio audio, double start, double end, JobSettings settings, JobSteps steps,
                                                   Action<string> log, CancellationToken cancel)
    {
        var preset = Presets.Get(settings.Preset);
        var modelPath = steps.FindModel(preset.Model)
                        ?? throw new EngineException($"The {preset.Model} model is not on this PC; make subtitles for a file first to download it.");
        var duration = audio.Samples.Length / (double)Media.Audio.SampleRate;
        var (lo, hi) = Window(start, end, duration);
        // the review read the audio with the job's clean-up; a preset that wants the other reads its part again
        var enhance = Enhances(settings);
        var clip = enhance == audio.Enhanced
            ? FileTranscription.Slice(audio.Samples, lo, hi)
            : await steps.ReadAudioPart(audio.Path, lo, hi - lo, enhance, cancel);
        var device = steps.Cache.DeviceOf(modelPath) ?? (steps.CudaDeviceCount() > 0 ? "cuda" : "cpu");
        log($"Re-running {Review.CueTime.Range(start, end)} with {preset.Name} ({Py.F0(hi - lo)} s of audio with the context around it)...");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var model = steps.Cache.Get(modelPath, device, steps.LoadModel,
                                            () => log($"Loading '{preset.Model}' on {(device == "cuda" ? "GPU" : "CPU")}..."));
                var decoded = FileTranscription.DecodeWords(model, clip, settings, preset, cancel,
                                                            repetitionPenalty: FileTranscription.LoopRepetitionPenalty);
                var speech = steps.SpeechProbabilities(clip, cancel);
                var words = settings.TrimWordEdges ? WordEdges.Trim(decoded, speech) : decoded;
                return UnheardCues.Drop(ToCues(MapWords(words, lo, start, end), start, end), speech, lo);
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

/// <summary>The audio a part is re-run from.</summary>
/// <param name="Path">the file, to read a part of again when a preset wants the other clean-up</param>
/// <param name="Samples">the whole file's 16 kHz audio, as the review read it</param>
/// <param name="Enhanced">whether <paramref name="Samples"/> went through ffmpeg's clean-up</param>
public sealed record PartAudio(string Path, float[] Samples, bool Enhanced);
