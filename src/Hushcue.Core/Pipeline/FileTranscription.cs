using Hushcue.Core.Engine;
using Hushcue.Core.Media;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// One file's audio to finished cues: decode the words, re-decode passages where Whisper looped, clean up.
/// </summary>
internal static class FileTranscription
{
    /// <summary>Context decoded on each side of a looped passage.</summary>
    public const double LoopPadSeconds = 2.0;

    /// <summary>Applied only when re-decoding a passage that looped.</summary>
    public const float LoopRepetitionPenalty = 1.3f;

    /// <summary>A short sentence in the style subtitles want (capitals, commas, question marks, full stops), which
    /// Whisper copies (<see cref="JobSettings.PunctuationPrompt"/>).</summary>
    public const string PunctuationPrompt = "Hi, how are you? I'm fine, thanks. Well, see you later.";

    /// <summary>
    /// Decode, re-decode looped passages (when <see cref="JobSettings.RecoverLoops"/>), cap what still loops, and
    /// build the cues.
    /// </summary>
    /// <param name="onFraction">how far the first decode is through the audio, 0..1</param>
    /// <param name="onWords">each decoded segment's words as they come, before loop recovery and the clean-up</param>
    public static List<Cue> Transcribe(ITranscriber model, float[] audio, JobSettings settings, Preset preset,
                                       Action<double> onFraction, Action<string> log, CancellationToken cancel,
                                       Action<IReadOnlyList<Word>>? onWords = null) =>
        Cleanup.ToCues(TranscribeWords(model, audio, settings, preset, onFraction, log, cancel, onWords)); // collapse_repeats, then the cues

    /// <summary>The words <see cref="Transcribe"/> makes its cues from: decoded, with looped passages re-decoded.</summary>
    public static List<Word> TranscribeWords(ITranscriber model, float[] audio, JobSettings settings, Preset preset,
                                             Action<double> onFraction, Action<string> log, CancellationToken cancel,
                                             Action<IReadOnlyList<Word>>? onWords = null)
    {
        var words = DecodeWords(model, audio, settings, preset, cancel, onFraction, log: log, onWords: onWords);
        if (settings.RecoverLoops)
            words = RecoverLoops(model, audio, words, settings, preset, log, cancel);
        return words;
    }

    /// <summary>Run Whisper on 16 kHz audio and return its words without non-speech markers or stretched letters.</summary>
    /// <param name="log">where to say how much speech voice activity detection kept; null says nothing</param>
    /// <param name="onWords">each kept segment's words, as soon as it is decoded</param>
    public static List<Word> DecodeWords(ITranscriber model, float[] audio, JobSettings settings, Preset preset,
                                         CancellationToken cancel, Action<double>? onFraction = null,
                                         float repetitionPenalty = 1, Action<string>? log = null,
                                         Action<IReadOnlyList<Word>>? onWords = null)
    {
        var options = new TranscribeOptions
        {
            Language = settings.Language,
            Task = "transcribe",
            BeamSize = preset.BeamSize,
            Temperatures = preset.Temperatures,
            ConditionOnPreviousText = settings.PreviousText,
            RepetitionPenalty = repetitionPenalty,
            Vad = Presets.Vad(preset, settings),
            NoSpeechThreshold = 0.6,
            LogProbThreshold = -1.0,
            CompressionRatioThreshold = 2.4,
            WordTimestamps = true,
            HallucinationSilenceThreshold = 2.0,
            Hotwords = Hotwords(settings),
        };
        var (segments, info) = model.Transcribe(audio, options, cancel);
        if (options.Vad is not null && log is not null)
            log($"Skipping silence: {info.DurationAfterVad:F0} s of {info.Duration:F0} s contain speech");
        var total = Math.Max(info.Duration, 1.0);
        var words = new List<Word>();
        foreach (var segment in segments)
        {
            cancel.ThrowIfCancellationRequested();
            onFraction?.Invoke(Math.Min(segment.End / total, 1.0));
            if (segment.NoSpeechProb > 0.6 && segment.AvgLogprob < -1.0)
                continue;
            var segmentWords = segment.Words is { Count: > 0 }
                ? segment.Words.Select(w => new Word(w.Text, w.Start, w.End, w.Probability))
                : [new Word(segment.Text, segment.Start, segment.End)];
            var kept = Cleanup.StripMarkers(segmentWords);
            words.AddRange(kept);
            if (onWords is not null && kept.Count > 0)
                onWords(Cleanup.SquashStretched(kept));
        }
        return Cleanup.SquashStretched(words);
    }

    /// <summary>
    /// The punctuation prompt and the vocabulary, as hotwords. Hotwords, not the initial prompt: without previous-text
    /// context the initial prompt only reaches the first 30 s window, hotwords go into every window. Null for none.
    /// </summary>
    internal static string? Hotwords(JobSettings settings)
    {
        string[] parts = [settings.PunctuationPrompt ? PunctuationPrompt : "", Py.Strip(settings.Vocabulary)];
        var hotwords = string.Join(' ', parts.Where(p => p.Length > 0));
        return hotwords.Length > 0 ? hotwords : null;
    }

    /// <summary>
    /// The samples of [start, end) seconds: from
    /// int(start * rate), int((end - start) * rate) of them, cut short at the end of the audio.
    /// </summary>
    public static float[] Slice(float[] audio, double start, double end)
    {
        var first = (int)Math.Min((long)(start * Audio.SampleRate), audio.Length);
        var count = (int)Math.Min(Math.Max((long)((end - start) * Audio.SampleRate), 0), audio.Length - first);
        return audio.AsSpan(first, count).ToArray();
    }

    /// <summary>
    /// Re-decode looped passages in isolation. Over a long file, greedy decoding can fall into a loop ("Mmm," x112 over
    /// 27 s) that hides what was said; the same audio decoded as its own short clip, with a repetition penalty, does
    /// not loop. Each loop is padded with <see cref="LoopPadSeconds"/> of context, nearby loops are merged, and the
    /// words inside the padded span are replaced by the re-decoded ones. If the re-decode finds nothing, the original
    /// words are kept. One pass only; anything that still loops is capped afterwards by CollapseRepeats.
    /// </summary>
    public static List<Word> RecoverLoops(ITranscriber model, float[] audio, List<Word> words, JobSettings settings,
                                          Preset preset, Action<string> log, CancellationToken cancel)
    {
        var spans = new List<(double Lo, double Hi)>();
        foreach (var (i, covered, _) in Cleanup.FindRepeatRuns(words))
        {
            var lo = Math.Max(0.0, words[i].Start - LoopPadSeconds);
            var hi = words[i + covered - 1].End + LoopPadSeconds;
            if (spans.Count > 0 && lo <= spans[^1].Hi)
                spans[^1] = (spans[^1].Lo, Math.Max(spans[^1].Hi, hi));
            else
                spans.Add((lo, hi));
        }
        if (spans.Count == 0)
            return words;
        var seconds = spans.Sum(s => s.Hi - s.Lo);
        log($"Re-decoding {spans.Count} looped passage(s) ({Py.F0(seconds)} s of audio)...");
        foreach (var (lo, hi) in spans)
        {
            cancel.ThrowIfCancellationRequested();
            var fixedWords = DecodeWords(model, Slice(audio, lo, hi), settings, preset, cancel,
                                         repetitionPenalty: LoopRepetitionPenalty);
            if (fixedWords.Count == 0)
                continue;
            // a stable sort, as Python's list.sort
            words = [.. words.Where(w => !(lo <= w.Start && w.Start < hi))
                             .Concat(fixedWords.Select(w => w with { Start = w.Start + lo, End = w.End + lo }))
                             .OrderBy(w => w.Start)];
        }
        return words;
    }
}
