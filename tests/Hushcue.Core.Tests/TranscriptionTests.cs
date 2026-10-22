using Hushcue.Core.Pipeline;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

/// <summary>
/// Loop recovery, transcribe_file and the presets: the 7 model tests of SubGen's test_pipeline.py (with a fake model),
/// plus the VAD options SubGen's _decode_words uses and its WAV slicing.
/// </summary>
public class TranscriptionTests
{
    private const int Rate = 16000;
    private static readonly float[] Silence = new float[20 * Rate]; // SubGen's 20 s silent WAV
    private static readonly Preset Balanced = Presets.Get("Balanced");

    private static Word W(string text, double start, double end) => new(text, start, end);

    private static List<Word> LoopWords()
    {
        var hum = Enumerable.Range(0, 8).Select(i => W(" mmm,", 5 + i * .5, 5 + i * .5 + .4)); // 5.0 .. 8.9
        return [W(" hi", 1, 1.3), .. hum, W(" bye", 15, 15.3)];
    }

    private static List<Word> Recover(FakeTranscriber model, List<Word> words, CancellationToken cancel = default) =>
        FileTranscription.RecoverLoops(model, Silence, words, new JobSettings(), Balanced, _ => { }, cancel);

    [Fact]
    public void RecoverLoopsReplacesLoopedPassage()
    {
        Word[] clip = [W(" that", 2.1, 2.4), W(" feels", 2.5, 2.9), W(" good", 3.0, 3.4)]; // relative to the clip start (3.0 s)
        var model = new FakeTranscriber(LoopWords(), clip, Silence.Length);

        var output = Recover(model, LoopWords());

        Assert.Equal(["hi", "that", "feels", "good", "bye"], output.Select(w => w.Text.Trim()));
        Assert.Equal([1.0, 5.1, 5.5, 6.0, 15.0], output.Select(w => Math.Round(w.Start, 1))); // shifted by the 3.0 s offset
        var call = model.Calls[0];
        Assert.False(call.Whole);
        Assert.Equal(FileTranscription.LoopRepetitionPenalty, call.RepetitionPenalty);
        Assert.Equal((int)Math.Round((10.9 - 3.0) * Rate), call.Samples); // 2 s of padding either side
    }

    [Fact]
    public void RecoverLoopsKeepsOriginalWhenRedecodeIsEmpty()
    {
        var words = LoopWords();
        Assert.Equal(words, Recover(new FakeTranscriber(words, [], Silence.Length), [.. words]));
    }

    [Fact]
    public void RecoverLoopsLeavesCleanTranscriptsAlone()
    {
        List<Word> words = [W(" hello", 1, 1.4), W(" there", 1.5, 1.9), W(" yes", 2, 2.3), W(" yes", 2.4, 2.7)];
        var model = new FakeTranscriber(words, [], Silence.Length);
        Assert.Equal(words, Recover(model, [.. words]));
        Assert.Empty(model.Calls);
    }

    [Fact]
    public void RecoverLoopsMergesNearbyLoopsIntoOneDecode()
    {
        var a = Enumerable.Range(0, 8).Select(i => W(" oh,", 2 + i * .25, 2.15 + i * .25));
        var b = Enumerable.Range(0, 8).Select(i => W(" yes,", 6 + i * .25, 6.15 + i * .25));
        List<Word> words = [.. a, .. b];
        var model = new FakeTranscriber(words, [W(" ok", 1, 1.2)], Silence.Length);
        Recover(model, words);
        Assert.Single(model.Calls);
    }

    [Fact]
    public void TranscribeFileUsesRecoveryAndCanDisableIt()
    {
        Word[] clip = [W(" that", 2.1, 2.4), W(" feels", 2.5, 2.9), W(" good", 3.0, 3.4)];
        string Text(List<Cue> cues) => string.Join(" ", cues.Select(c => c.Text));

        var on = FileTranscription.Transcribe(new FakeTranscriber(LoopWords(), clip, Silence.Length), Silence,
                                              new JobSettings(), Balanced, _ => { }, _ => { }, default);
        Assert.Contains("that feels good", Text(on));

        var offModel = new FakeTranscriber(LoopWords(), clip, Silence.Length);
        var off = FileTranscription.Transcribe(offModel, Silence, new JobSettings { RecoverLoops = false }, Balanced,
                                               _ => { }, _ => { }, default);
        Assert.DoesNotContain("that feels good", Text(off));
        Assert.Contains("mmm", Text(off));
        Assert.Single(offModel.Calls);
    }

    [Fact]
    public void RecoverLoopsHonoursCancel()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => Recover(new FakeTranscriber(LoopWords(), [], Silence.Length), LoopWords(), cancel.Token));
    }

    [Fact]
    public void PresetsDecodeDeterministically() =>
        // Temperature fallback samples randomly: identical runs disagreed on ~38% of words.
        Assert.All(Presets.All, preset => Assert.Equal([0.0f], preset.Temperatures));

    [Fact]
    public void RecoverLoopsIsOnByDefault() => Assert.True(new JobSettings().RecoverLoops);

    [Fact]
    public void DecodeUsesSubGensOptions()
    {
        var model = new FakeTranscriber([W(" hi", 0, 1)]);
        FileTranscription.DecodeWords(model, Silence, new JobSettings { Vocabulary = "  Anna \n" }, Balanced, default);
        var options = model.Calls[0].Options;
        Assert.Equal(("en", 5, false, 1f), (options.Language, options.BeamSize, options.ConditionOnPreviousText, options.RepetitionPenalty));
        Assert.Equal((0.6, -1.0, 2.4, 2.0), (options.NoSpeechThreshold, options.LogProbThreshold,
                                             options.CompressionRatioThreshold, options.HallucinationSilenceThreshold));
        Assert.True(options.WordTimestamps);
        Assert.Equal("Anna", options.InitialPrompt);
        Assert.Null(options.Vad);
    }

    [Theory]
    [InlineData("Fast", false, 0.1)]
    [InlineData("Fast", true, 0.1)]
    [InlineData("Balanced", true, 0.1)]
    [InlineData("Thorough", true, 0.1)]
    public void VadUsesSubGensOptions(string preset, bool skipSilence, double threshold)
    {
        // pipeline._decode_words: threshold = preset's or 0.1, min_silence = preset's or 1000, speech_pad_ms = 500
        var vad = Presets.Vad(Presets.Get(preset), new JobSettings { SkipSilence = skipSilence });
        Assert.NotNull(vad);
        Assert.Equal((threshold, 1000, 500), (vad.Threshold, vad.MinSilenceDurationMs, vad.SpeechPadMs));
        // the rest are faster-whisper's VadOptions defaults
        Assert.Equal((null, 0, double.PositiveInfinity), (vad.NegThreshold, vad.MinSpeechDurationMs, vad.MaxSpeechDurationS));
    }

    [Fact]
    public void NoVadWithoutPresetOrSkipSilence()
    {
        Assert.Null(Presets.Vad(Presets.Get("Balanced"), new JobSettings()));
        Assert.Null(Presets.Vad(Presets.Get("Thorough"), new JobSettings()));
    }

    [Fact]
    public void SliceReadsLikeSubGensWavSlice()
    {
        var audio = Enumerable.Range(0, 3 * Rate).Select(i => (float)i).ToArray();
        // int(start * rate) samples in, int((end - start) * rate) of them
        var slice = FileTranscription.Slice(audio, 0.30001, 1.2);
        Assert.Equal(4800f, slice[0]);
        Assert.Equal((int)((1.2 - 0.30001) * Rate), slice.Length);
        // cut short at the end; nothing past it
        Assert.Equal(Rate, FileTranscription.Slice(audio, 2, 9).Length);
        Assert.Empty(FileTranscription.Slice(audio, 5, 9));
    }
}
