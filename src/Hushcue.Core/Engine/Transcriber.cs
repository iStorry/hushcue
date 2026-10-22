using System.IO.Compression;
using System.Text;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Engine;

/// <summary>Options of faster-whisper's WhisperModel.transcribe, with its defaults.</summary>
public sealed record TranscribeOptions
{
    public string? Language { get; init; }
    public string Task { get; init; } = "transcribe";
    public int BeamSize { get; init; } = 5;
    public int BestOf { get; init; } = 5;
    public float Patience { get; init; } = 1;
    public float LengthPenalty { get; init; } = 1;
    public float RepetitionPenalty { get; init; } = 1;
    public int NoRepeatNgramSize { get; init; }
    public IReadOnlyList<float> Temperatures { get; init; } = [0.0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f];
    public double? CompressionRatioThreshold { get; init; } = 2.4;
    public double? LogProbThreshold { get; init; } = -1.0;
    public double? NoSpeechThreshold { get; init; } = 0.6;
    public bool ConditionOnPreviousText { get; init; } = true;
    public double PromptResetOnTemperature { get; init; } = 0.5;
    public string? InitialPrompt { get; init; }
    public string? Prefix { get; init; }
    public bool SuppressBlank { get; init; } = true;
    /// <summary>-1 adds the non-speech symbol tokens; null suppresses nothing.</summary>
    public IReadOnlyList<int>? SuppressTokens { get; init; } = [-1];
    public bool WithoutTimestamps { get; init; }
    public double MaxInitialTimestamp { get; init; } = 1.0;
    public bool WordTimestamps { get; init; }
    public string PrependPunctuations { get; init; } = "\"'“¿([{-";
    public string AppendPunctuations { get; init; } = "\"'.。,，!！?？:：”)]}、";
    public int? MaxNewTokens { get; init; }
    /// <summary>start,end,start,end,... in seconds; null means the whole file.</summary>
    public IReadOnlyList<double>? ClipTimestamps { get; init; }
    public double? HallucinationSilenceThreshold { get; init; }
    public string? Hotwords { get; init; }
    public double LanguageDetectionThreshold { get; init; } = 0.5;
    public int LanguageDetectionSegments { get; init; } = 1;
    /// <summary>vad_filter and vad_parameters: transcribe only the speech Silero VAD finds; null transcribes
    /// everything. Ignored when <see cref="ClipTimestamps"/> is set.</summary>
    public VadOptions? Vad { get; init; }
}

/// <summary>A word with its timing, mutable while word timestamps are being refined.</summary>
public sealed class TimedWord(string text, double start, double end, double probability)
{
    public string Text { get; set; } = text;
    public double Start { get; set; } = start;
    public double End { get; set; } = end;
    public double Probability { get; set; } = probability;
}

public sealed record Segment(int Id, int Seek, double Start, double End, string Text, IReadOnlyList<int> Tokens,
                             double Temperature, double AvgLogprob, double CompressionRatio, double NoSpeechProb,
                             IReadOnlyList<TimedWord>? Words);

/// <param name="Duration">seconds of audio given</param>
/// <param name="DurationAfterVad">seconds of it transcribed: all of it, or the speech voice activity detection kept</param>
public sealed record TranscriptionInfo(string Language, double LanguageProbability, double Duration, double DurationAfterVad);

/// <summary>What the subtitle job needs from Whisper: faster-whisper's WhisperModel.transcribe.</summary>
public interface ITranscriber
{
    /// <summary>Transcribe 16 kHz mono audio; segments may be produced lazily.</summary>
    (IEnumerable<Segment> Segments, TranscriptionInfo Info) Transcribe(float[] audio, TranscribeOptions options,
                                                                       CancellationToken cancel = default);
}

/// <summary>
/// faster-whisper's transcription loop (WhisperModel.transcribe / generate_segments) on top of CTranslate2: 30 s
/// windows, temperature fallback, segment splitting on timestamp tokens, word timestamps from cross-attention
/// alignment, and skipping silence before likely hallucinations. A port of faster-whisper 1.2.1, kept line for line
/// so its output can be checked against faster-whisper's.
/// </summary>
public sealed class Transcriber : ITranscriber
{
    private const int InputStride = 2;
    private const int FramesPerSecond = MelSpectrogram.SampleRate / MelSpectrogram.HopLength; // 100
    private const int TokensPerSecond = MelSpectrogram.SampleRate / (MelSpectrogram.HopLength * InputStride); // 50
    private const double TimePrecision = 0.02;
    private const int MaxLength = 448;
    private const string Punctuation = "\"'“¿([{-\"'.。,，!！?？:：”)]}、";

    private readonly WhisperModel _model;
    private readonly string _tokenizerPath;
    private readonly MelSpectrogram _mel;

    public Transcriber(WhisperModel model)
    {
        _model = model;
        _tokenizerPath = Path.Combine(model.Path, "tokenizer.json");
        _mel = new MelSpectrogram(WhisperModel.FeatureSize(model.Path));
    }

    /// <summary>
    /// Computes the log-mel features of the audio that is decoded (after voice activity detection): NMels rows and the
    /// number of frames. Null uses <see cref="MelSpectrogram"/>; benchmarks substitute faster-whisper's own features to
    /// compare decoding bit for bit.
    /// </summary>
    public Func<float[], (float[] Features, int Frames)>? FeatureSource { get; init; }

    /// <summary>
    /// Transcribe 16 kHz mono audio. Voice activity detection, when asked for, runs first; segments are then produced
    /// lazily, one decoding window at a time, like faster-whisper's generator. <paramref name="cancel"/> is checked
    /// before every window.
    /// </summary>
    public (IEnumerable<Segment> Segments, TranscriptionInfo Info) Transcribe(
        float[] audio, TranscribeOptions options, CancellationToken cancel = default)
    {
        var speech = options.Vad is { } vad && options.ClipTimestamps is null ? SileroVad.SpeechChunks(audio, vad, cancel) : null;
        var kept = speech is null ? audio : SpeechTimestamps.Collect(audio, speech);
        var (features, frames) = FeatureSource is { } source ? source(kept) : _mel.Compute(kept);
        var (segments, info) = TranscribeFeatures(features, frames, Seconds(audio), options, cancel);
        info = info with { DurationAfterVad = Seconds(kept) };
        return (speech is { Count: > 0 } ? SpeechTimestamps.Restore(segments, speech) : segments, info);
    }

    private static double Seconds(float[] audio) => audio.Length / (double)MelSpectrogram.SampleRate;

    /// <summary>Transcribe precomputed log-mel features (NMels rows of <paramref name="frames"/> values).</summary>
    public (IEnumerable<Segment> Segments, TranscriptionInfo Info) TranscribeFeatures(
        float[] features, int frames, double duration, TranscribeOptions options, CancellationToken cancel = default)
    {

        string language;
        double languageProbability;
        if (options.Language is null)
        {
            if (!_model.IsMultilingual)
            {
                (language, languageProbability) = ("en", 1);
            }
            else
            {
                var startTimestamp = options.ClipTimestamps is { Count: > 0 } clips ? clips[0] : 0.0;
                var contentFrames = frames - 1;
                var seek = startTimestamp * FramesPerSecond < contentFrames ? (int)(startTimestamp * FramesPerSecond) : 0;
                (language, languageProbability) = DetectLanguage(features, frames, seek, options);
            }
        }
        else
        {
            language = !_model.IsMultilingual ? "en" : options.Language;
            languageProbability = 1;
        }

        var tokenizer = new WhisperTokenizer(_tokenizerPath, _model.IsMultilingual, options.Task, language);
        var suppress = options.SuppressTokens is { Count: > 0 } ? SuppressedTokens(tokenizer, options.SuppressTokens) : options.SuppressTokens;
        var info = new TranscriptionInfo(language, languageProbability, duration, duration);
        return (GenerateSegments(features, frames, tokenizer, options, suppress, cancel), info);
    }

    private (string, double) DetectLanguage(float[] features, int frames, int seek, TranscribeOptions options)
    {
        var available = Math.Min(frames - seek, options.LanguageDetectionSegments * MelSpectrogram.FramesPerWindow);
        var votes = new Dictionary<string, List<double>>();
        for (var i = 0; i < available; i += MelSpectrogram.FramesPerWindow)
        {
            var window = _mel.Window3000(features, frames, seek + i, Math.Min(MelSpectrogram.FramesPerWindow, available - i));
            using var encoded = _model.Encode(window);
            var (token, probability) = _model.DetectLanguage(encoded)[0];
            var code = token[2..^2];
            if (probability > options.LanguageDetectionThreshold)
                return (code, probability);
            if (!votes.TryGetValue(code, out var list))
                votes[code] = list = [];
            list.Add(probability);
        }
        // No window was sure: the language most windows voted for, with its best probability.
        var best = votes.First();
        foreach (var vote in votes)
        {
            if (vote.Value.Count > best.Value.Count)
                best = vote;
        }
        return (best.Key, best.Value.Max());
    }

    private sealed class SubSegment(int seek, double start, double end, List<int> tokens)
    {
        public int Seek { get; } = seek;
        public double Start { get; set; } = start;
        public double End { get; set; } = end;
        public List<int> Tokens { get; } = tokens;
        public List<TimedWord> Words { get; set; } = [];
    }

    private IEnumerable<Segment> GenerateSegments(float[] features, int frames, WhisperTokenizer tokenizer,
                                                  TranscribeOptions options, IReadOnlyList<int>? suppressTokens,
                                                  CancellationToken cancel)
    {
        var contentFrames = frames - 1;
        var contentDuration = contentFrames * MelSpectrogram.TimePerFrame;

        var seekPoints = (options.ClipTimestamps ?? [0.0]).Select(ts => (int)Py.Round(ts * FramesPerSecond)).ToList();
        if (seekPoints.Count == 0)
            seekPoints.Add(0);
        if (seekPoints.Count % 2 == 1)
            seekPoints.Add(contentFrames);
        var seekClips = Enumerable.Range(0, seekPoints.Count / 2).Select(i => (seekPoints[2 * i], seekPoints[2 * i + 1])).ToList();

        var idx = 0;
        var clipIdx = 0;
        var seek = seekClips[clipIdx].Item1;
        var allTokens = new List<int>();
        var promptResetSince = 0;
        if (options.InitialPrompt is not null)
            allTokens.AddRange(tokenizer.Encode(" " + Py.Strip(options.InitialPrompt)));

        var lastSpeechTimestamp = 0.0;
        while (clipIdx < seekClips.Count)
        {
            cancel.ThrowIfCancellationRequested();
            var (seekClipStart, seekClipEnd) = seekClips[clipIdx];
            if (seekClipEnd > contentFrames)
                seekClipEnd = contentFrames;
            if (seek < seekClipStart)
                seek = seekClipStart;
            if (seek >= seekClipEnd)
            {
                clipIdx++;
                if (clipIdx < seekClips.Count)
                    seek = seekClips[clipIdx].Item1;
                continue;
            }
            var timeOffset = seek * MelSpectrogram.TimePerFrame;
            var windowEndTime = (seek + MelSpectrogram.FramesPerWindow) * MelSpectrogram.TimePerFrame;
            var segmentSize = Math.Min(Math.Min(MelSpectrogram.FramesPerWindow, contentFrames - seek), seekClipEnd - seek);
            var segmentDuration = segmentSize * MelSpectrogram.TimePerFrame;
            var window = _mel.Window3000(features, frames, seek, segmentSize);
            var previousTokens = allTokens.GetRange(promptResetSince, allTokens.Count - promptResetSince);

            List<Segment> output = [];
            double temperature;
            using (var encoded = _model.Encode(window))
            {
                var prompt = GetPrompt(tokenizer, previousTokens, options.WithoutTimestamps,
                                       seek == 0 ? options.Prefix : null, options.Hotwords);
                var (result, avgLogprob, chosenTemperature, compressionRatio) =
                    GenerateWithFallback(encoded, prompt, tokenizer, options, suppressTokens);
                temperature = chosenTemperature;

                if (options.NoSpeechThreshold is { } noSpeechThreshold)
                {
                    // no voice activity check
                    var shouldSkip = result.NoSpeechProb > noSpeechThreshold;
                    if (options.LogProbThreshold is { } logProbThreshold && avgLogprob > logProbThreshold)
                        shouldSkip = false; // don't skip if the logprob is high enough, despite the no_speech_prob
                    if (shouldSkip)
                    {
                        seek += segmentSize; // fast-forward to the next segment boundary
                        continue;
                    }
                }

                var tokens = result.SequencesIds[0].ToList();
                var previousSeek = seek;
                var (currentSegments, newSeek, singleTimestampEnding) = SplitSegmentsByTimestamps(
                    tokenizer, tokens, timeOffset, segmentSize, segmentDuration, seek);
                seek = newSeek;

                if (options.WordTimestamps)
                {
                    AddWordTimestamps(currentSegments, tokenizer, encoded, segmentSize, options.PrependPunctuations,
                                      options.AppendPunctuations, lastSpeechTimestamp);
                    if (!singleTimestampEnding)
                    {
                        var lastWordEnd = GetEnd(currentSegments);
                        if (lastWordEnd is { } end && end > timeOffset)
                            seek = (int)Py.Round(end * FramesPerSecond);
                    }

                    // skip silence before possible hallucinations
                    if (options.HallucinationSilenceThreshold is { } threshold)
                    {
                        // if first segment might be a hallucination, skip leading silence
                        var firstSegment = NextWordsSegment(currentSegments);
                        if (firstSegment is not null && IsSegmentAnomaly(firstSegment))
                        {
                            var gap = firstSegment.Start - timeOffset;
                            if (gap > threshold)
                            {
                                seek = previousSeek + (int)Py.Round(gap * FramesPerSecond);
                                continue;
                            }
                        }

                        // skip silence before any possible hallucination that is surrounded by silence or more
                        // hallucinations
                        var halLastEnd = lastSpeechTimestamp;
                        for (var si = 0; si < currentSegments.Count; si++)
                        {
                            var segment = currentSegments[si];
                            if (segment.Words.Count == 0)
                                continue;
                            if (IsSegmentAnomaly(segment))
                            {
                                var nextSegment = NextWordsSegment(currentSegments.Skip(si + 1));
                                var halNextStart = nextSegment is not null ? nextSegment.Words[0].Start : timeOffset + segmentDuration;
                                var silenceBefore = segment.Start - halLastEnd > threshold
                                                    || segment.Start < threshold
                                                    || segment.Start - timeOffset < 2.0;
                                var silenceAfter = halNextStart - segment.End > threshold
                                                   || IsSegmentAnomaly(nextSegment)
                                                   || windowEndTime - segment.End < 2.0;
                                if (silenceBefore && silenceAfter)
                                {
                                    seek = (int)Py.Round(Math.Max(timeOffset + 1, segment.Start) * FramesPerSecond);
                                    if (contentDuration - segment.End < threshold)
                                        seek = contentFrames;
                                    currentSegments.RemoveRange(si, currentSegments.Count - si);
                                    break;
                                }
                            }
                            halLastEnd = segment.End;
                        }
                    }

                    if (GetEnd(currentSegments) is { } lastEnd)
                        lastSpeechTimestamp = lastEnd;
                }

                foreach (var segment in currentSegments)
                {
                    var text = tokenizer.Decode(segment.Tokens);
                    if (segment.Start == segment.End || Py.Strip(text).Length == 0)
                        continue;
                    allTokens.AddRange(segment.Tokens);
                    idx++;
                    output.Add(new Segment(idx, previousSeek, segment.Start, segment.End, text, segment.Tokens,
                                           temperature, avgLogprob, compressionRatio, result.NoSpeechProb,
                                           options.WordTimestamps ? segment.Words : null));
                }
            }

            foreach (var segment in output)
                yield return segment;

            if (!options.ConditionOnPreviousText || temperature > options.PromptResetOnTemperature)
                promptResetSince = allTokens.Count;
        }
    }

    // anomalous words are very long/short/improbable
    private static double WordAnomalyScore(TimedWord word)
    {
        var duration = word.End - word.Start;
        var score = 0.0;
        if (word.Probability < 0.15)
            score += 1.0;
        if (duration < 0.133)
            score += (0.133 - duration) * 15;
        if (duration > 2.0)
            score += duration - 2.0;
        return score;
    }

    private static bool IsSegmentAnomaly(SubSegment? segment)
    {
        if (segment is null || segment.Words.Count == 0)
            return false;
        var words = segment.Words.Where(w => !Punctuation.Contains(w.Text, StringComparison.Ordinal)).Take(8).ToList();
        var score = words.Sum(WordAnomalyScore);
        return score >= 3 || score + 0.01 >= words.Count;
    }

    private static SubSegment? NextWordsSegment(IEnumerable<SubSegment> segments) =>
        segments.FirstOrDefault(s => s.Words.Count > 0);

    private static double? GetEnd(List<SubSegment> segments)
    {
        for (var s = segments.Count - 1; s >= 0; s--)
        {
            if (segments[s].Words.Count > 0)
                return segments[s].Words[^1].End;
        }
        return segments.Count > 0 ? segments[^1].End : null;
    }

    private static (List<SubSegment>, int Seek, bool SingleTimestampEnding) SplitSegmentsByTimestamps(
        WhisperTokenizer tokenizer, List<int> tokens, double timeOffset, int segmentSize, double segmentDuration, int seek)
    {
        var begin = tokenizer.TimestampBegin;
        var currentSegments = new List<SubSegment>();
        var singleTimestampEnding = tokens.Count >= 2 && tokens[^2] < begin && begin <= tokens[^1];
        var consecutive = Enumerable.Range(1, Math.Max(tokens.Count - 1, 0))
            .Where(i => tokens[i] >= begin && tokens[i - 1] >= begin).ToList();

        if (consecutive.Count > 0)
        {
            var slices = new List<int>(consecutive);
            if (singleTimestampEnding)
                slices.Add(tokens.Count);
            var lastSlice = 0;
            foreach (var currentSlice in slices)
            {
                var sliced = tokens.GetRange(lastSlice, currentSlice - lastSlice);
                var startPosition = sliced[0] - begin;
                var endPosition = sliced[^1] - begin;
                currentSegments.Add(new SubSegment(seek, timeOffset + startPosition * TimePrecision,
                                                   timeOffset + endPosition * TimePrecision, sliced));
                lastSlice = currentSlice;
            }
            if (singleTimestampEnding)
            {
                // single timestamp at the end means no speech after the last timestamp.
                seek += segmentSize;
            }
            else
            {
                // otherwise, ignore the unfinished segment and seek to the last timestamp
                var lastTimestampPosition = tokens[lastSlice - 1] - begin;
                seek += lastTimestampPosition * InputStride;
            }
        }
        else
        {
            var duration = segmentDuration;
            var timestamps = tokens.Where(t => t >= begin).ToList();
            if (timestamps.Count > 0 && timestamps[^1] != begin)
                duration = (timestamps[^1] - begin) * TimePrecision;
            currentSegments.Add(new SubSegment(seek, timeOffset, timeOffset + duration, tokens));
            seek += segmentSize;
        }
        return (currentSegments, seek, singleTimestampEnding);
    }

    private (GenerationResult, double AvgLogprob, double Temperature, double CompressionRatio) GenerateWithFallback(
        EncodedWindow encoded, List<int> prompt, WhisperTokenizer tokenizer, TranscribeOptions options,
        IReadOnlyList<int>? suppressTokens)
    {
        var allResults = new List<(GenerationResult, double, double, double)>();
        var belowCrThresholdResults = new List<(GenerationResult, double, double, double)>();
        var maxInitialTimestampIndex = (int)Py.Round(options.MaxInitialTimestamp / TimePrecision);
        var maxLength = options.MaxNewTokens is { } maxNew ? prompt.Count + maxNew : MaxLength;
        if (maxLength > MaxLength)
            throw new ArgumentException($"The prompt ({prompt.Count} tokens) plus max_new_tokens exceeds {MaxLength}.");

        (GenerationResult, double, double, double) decodeResult = default;
        var lastTemperature = 0.0;
        foreach (var t in options.Temperatures)
        {
            lastTemperature = t;
            var settings = new GenerateSettings
            {
                LengthPenalty = options.LengthPenalty,
                RepetitionPenalty = options.RepetitionPenalty,
                NoRepeatNgramSize = options.NoRepeatNgramSize,
                MaxLength = maxLength,
                ReturnScores = true,
                ReturnNoSpeechProb = true,
                SuppressBlank = options.SuppressBlank,
                SuppressTokens = suppressTokens,
                MaxInitialTimestampIndex = maxInitialTimestampIndex,
            };
            settings = t > 0
                ? settings with { BeamSize = 1, NumHypotheses = options.BestOf, SamplingTopK = 0, SamplingTemperature = t }
                : settings with { BeamSize = options.BeamSize, Patience = options.Patience };

            var result = _model.Generate(encoded, prompt, settings);
            var tokens = result.SequencesIds[0];
            // Recover the average log prob from the returned score.
            var seqLen = tokens.Length;
            var cumLogprob = result.Scores[0] * Math.Pow(seqLen, options.LengthPenalty);
            var avgLogprob = cumLogprob / (seqLen + 1);
            var text = Py.Strip(tokenizer.Decode(tokens));
            var compressionRatio = CompressionRatio(text);

            decodeResult = (result, avgLogprob, (double)t, compressionRatio);
            allResults.Add(decodeResult);
            var needsFallback = false;
            if (options.CompressionRatioThreshold is { } crThreshold)
            {
                if (compressionRatio > crThreshold)
                    needsFallback = true; // too repetitive
                else
                    belowCrThresholdResults.Add(decodeResult);
            }
            if (options.LogProbThreshold is { } lpThreshold && avgLogprob < lpThreshold)
                needsFallback = true; // average log probability is too low
            if (options.NoSpeechThreshold is { } nsThreshold && result.NoSpeechProb > nsThreshold
                && options.LogProbThreshold is { } lp && avgLogprob < lp)
                needsFallback = false; // silence
            if (!needsFallback)
                return decodeResult;
        }

        // all failed, select the result with the highest average log probability
        var candidates = belowCrThresholdResults.Count > 0 ? belowCrThresholdResults : allResults;
        var best = candidates[0];
        foreach (var candidate in candidates)
        {
            if (candidate.Item2 > best.Item2)
                best = candidate;
        }
        // with the final temperature, for prompt_reset_on_temperature
        return (best.Item1, best.Item2, lastTemperature, best.Item4);
    }

    private static List<int> GetPrompt(WhisperTokenizer tokenizer, List<int> previousTokens, bool withoutTimestamps,
                                       string? prefix, string? hotwords)
    {
        var prompt = new List<int>();
        var useHotwords = !string.IsNullOrEmpty(hotwords) && string.IsNullOrEmpty(prefix);
        if (previousTokens.Count > 0 || useHotwords)
        {
            prompt.Add(tokenizer.SotPrev);
            if (useHotwords)
            {
                var hotwordsTokens = tokenizer.Encode(" " + Py.Strip(hotwords!));
                if (hotwordsTokens.Count >= MaxLength / 2)
                    hotwordsTokens = hotwordsTokens.GetRange(0, MaxLength / 2 - 1);
                prompt.AddRange(hotwordsTokens);
            }
            if (previousTokens.Count > 0)
                prompt.AddRange(previousTokens.Skip(Math.Max(0, previousTokens.Count - (MaxLength / 2 - 1))));
        }
        prompt.AddRange(tokenizer.SotSequence);
        if (withoutTimestamps)
            prompt.Add(tokenizer.NoTimestamps);
        if (!string.IsNullOrEmpty(prefix))
        {
            var prefixTokens = tokenizer.Encode(" " + Py.Strip(prefix));
            if (prefixTokens.Count >= MaxLength / 2)
                prefixTokens = prefixTokens.GetRange(0, MaxLength / 2 - 1);
            if (!withoutTimestamps)
                prompt.Add(tokenizer.TimestampBegin);
            prompt.AddRange(prefixTokens);
        }
        return prompt;
    }

    private sealed class Timing(string word, List<int> tokens, double start, double end, double probability)
    {
        public string Word { get; set; } = word;
        public List<int> Tokens { get; set; } = tokens;
        public double Start { get; set; } = start;
        public double End { get; set; } = end;
        public double Probability { get; } = probability;
    }

    private void AddWordTimestamps(List<SubSegment> segment, WhisperTokenizer tokenizer, EncodedWindow encoded,
                                   int numFrames, string prependPunctuations, string appendPunctuations,
                                   double lastSpeechTimestamp)
    {
        // faster-whisper handles a list of segment groups; generate_segments always passes exactly one.
        var segmentTokens = segment.Select(s => s.Tokens.Where(t => t < tokenizer.Eot).ToList()).ToList();
        var textTokens = segmentTokens.SelectMany(t => t).ToList();
        var alignment = FindAlignment(tokenizer, textTokens, encoded, numFrames);

        var wordDurations = alignment.Select(w => w.End - w.Start).Where(d => d != 0).ToList();
        var medianDuration = Math.Min(0.7, wordDurations.Count > 0 ? Median(wordDurations) : 0.0);
        var maxDuration = medianDuration * 2;

        // hack: truncate long words at sentence boundaries.
        // a better segmentation algorithm based on VAD should be able to replace this.
        if (wordDurations.Count > 0)
        {
            const string sentenceEndMarks = ".。!！?？";
            // ensure words at sentence boundaries are not longer than twice the median word duration.
            for (var i = 1; i < alignment.Count; i++)
            {
                if (alignment[i].End - alignment[i].Start > maxDuration)
                {
                    if (sentenceEndMarks.Contains(alignment[i].Word, StringComparison.Ordinal))
                        alignment[i].End = alignment[i].Start + maxDuration;
                    else if (sentenceEndMarks.Contains(alignment[i - 1].Word, StringComparison.Ordinal))
                        alignment[i].Start = alignment[i].End - maxDuration;
                }
            }
        }
        MergePunctuations(alignment, prependPunctuations, appendPunctuations);

        var wordIndex = 0;
        var timeOffset = segment[0].Seek / (double)FramesPerSecond;
        for (var subIdx = 0; subIdx < segment.Count; subIdx++)
        {
            var subsegment = segment[subIdx];
            var savedTokens = 0;
            var words = new List<TimedWord>();
            while (wordIndex < alignment.Count && savedTokens < segmentTokens[subIdx].Count)
            {
                var timing = alignment[wordIndex];
                if (timing.Word.Length > 0)
                {
                    // round() on a numpy float64: rint(x * 100) / 100
                    words.Add(new TimedWord(timing.Word, NumpyRound2(timeOffset + timing.Start),
                                            NumpyRound2(timeOffset + timing.End), timing.Probability));
                }
                savedTokens += timing.Tokens.Count;
                wordIndex++;
            }

            // hack: truncate long words at segment boundaries.
            // a better segmentation algorithm based on VAD should be able to replace this.
            if (words.Count > 0)
            {
                // ensure the first and second word after a pause is not longer than twice the median word duration.
                if (words[0].End - lastSpeechTimestamp > medianDuration * 4
                    && (words[0].End - words[0].Start > maxDuration
                        || (words.Count > 1 && words[1].End - words[0].Start > maxDuration * 2)))
                {
                    if (words.Count > 1 && words[1].End - words[1].Start > maxDuration)
                    {
                        var boundary = Math.Max(words[1].End / 2, words[1].End - maxDuration);
                        words[0].End = words[1].Start = boundary;
                    }
                    words[0].Start = Math.Max(0, words[0].End - maxDuration);
                }

                // prefer the segment-level start timestamp if the first word is too long.
                if (subsegment.Start < words[0].End && subsegment.Start - 0.5 > words[0].Start)
                    words[0].Start = Math.Max(0, Math.Min(words[0].End - medianDuration, subsegment.Start));
                else
                    subsegment.Start = words[0].Start;

                // prefer the segment-level end timestamp if the last word is too long.
                if (subsegment.End > words[^1].Start && subsegment.End + 0.5 < words[^1].End)
                    words[^1].End = Math.Max(words[^1].Start + medianDuration, subsegment.End);
                else
                    subsegment.End = words[^1].End;

                lastSpeechTimestamp = subsegment.End;
            }
            subsegment.Words = words;
        }
    }

    private List<Timing> FindAlignment(WhisperTokenizer tokenizer, List<int> textTokens, EncodedWindow encoded,
                                       int numFrames, int medianFilterWidth = 7)
    {
        var result = _model.Align(encoded, tokenizer.SotSequence, textTokens, numFrames, medianFilterWidth);
        var (words, wordTokens) = tokenizer.SplitToWordTokens([.. textTokens, tokenizer.Eot]);
        if (wordTokens.Count <= 1)
            return []; // eot only

        var wordBoundaries = new List<int> { 0 };
        foreach (var tokens in wordTokens.Take(wordTokens.Count - 1))
            wordBoundaries.Add(wordBoundaries[^1] + tokens.Count);
        if (wordBoundaries.Count <= 1)
            return [];

        // times where the text index moves on (the first pair always counts)
        var jumpTimes = new List<double>();
        for (var i = 0; i < result.Alignments.Length; i++)
        {
            if (i == 0 || result.Alignments[i].TextIndex != result.Alignments[i - 1].TextIndex)
                jumpTimes.Add(result.Alignments[i].TimeIndex / (double)TokensPerSecond);
        }

        var timings = new List<Timing>();
        for (var w = 0; w < wordBoundaries.Count - 1; w++)
        {
            int i = wordBoundaries[w], j = wordBoundaries[w + 1];
            double sum = 0;
            for (var k = i; k < j; k++)
                sum += result.TextTokenProbs[k];
            timings.Add(new Timing(words[w], wordTokens[w], jumpTimes[i], jumpTimes[j], sum / (j - i)));
        }
        return timings;
    }

    private static void MergePunctuations(List<Timing> alignment, string prepended, string appended)
    {
        // merge prepended punctuations
        var i = alignment.Count - 2;
        var j = alignment.Count - 1;
        while (i >= 0)
        {
            var previous = alignment[i];
            var following = alignment[j];
            if (previous.Word.StartsWith(' ') && prepended.Contains(Py.Strip(previous.Word), StringComparison.Ordinal))
            {
                // prepend it to the following word
                following.Word = previous.Word + following.Word;
                following.Tokens = [.. previous.Tokens, .. following.Tokens];
                previous.Word = "";
                previous.Tokens = [];
            }
            else
            {
                j = i;
            }
            i--;
        }

        // merge appended punctuations
        i = 0;
        j = 1;
        while (j < alignment.Count)
        {
            var previous = alignment[i];
            var following = alignment[j];
            if (!previous.Word.EndsWith(' ') && appended.Contains(following.Word, StringComparison.Ordinal))
            {
                // append it to the previous word
                previous.Word += following.Word;
                previous.Tokens = [.. previous.Tokens, .. following.Tokens];
                following.Word = "";
                following.Tokens = [];
            }
            else
            {
                i = j;
            }
            j++;
        }
    }

    private static int[] SuppressedTokens(WhisperTokenizer tokenizer, IReadOnlyList<int> suppressTokens)
    {
        var tokens = new List<int>();
        if (suppressTokens.Contains(-1))
        {
            tokens.AddRange(suppressTokens.Where(t => t >= 0));
            tokens.AddRange(tokenizer.NonSpeechTokens());
        }
        else
        {
            tokens.AddRange(suppressTokens);
        }
        tokens.AddRange([tokenizer.Transcribe, tokenizer.Translate, tokenizer.Sot, tokenizer.SotPrev, tokenizer.SotLm,
                         tokenizer.NoSpeech]);
        return [.. tokens.Distinct().Order()];
    }

    /// <summary>len(text) / len(zlib.compress(text)) on the UTF-8 bytes.</summary>
    private static double CompressionRatio(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(bytes);
        return bytes.Length / (double)compressed.Length;
    }

    private static double NumpyRound2(double x) => Math.Round(x * 100.0, MidpointRounding.ToEven) / 100.0;

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
