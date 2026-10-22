using System.Runtime.InteropServices;
using System.Text.Json;
using Hushcue.Core.Engine;

namespace Hushcue.Core.Tests;

/// <summary>
/// Voice activity detection against faster-whisper's own results on speech with gaps of different lengths
/// (fixtures from tools/bench/make_vad_fixtures.py).
/// </summary>
public class VadTests
{
    private static readonly JsonElement Fixture =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "vad.json"))).RootElement;

    private static float[] Audio()
    {
        var pcm = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "vad-input.s16")));
        var audio = new float[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
            audio[i] = pcm[i] / 32768.0f;
        return audio;
    }

    private static float[] Padded(float[] audio)
    {
        var padded = new float[audio.Length + SileroVad.WindowSamples - audio.Length % SileroVad.WindowSamples];
        audio.CopyTo(padded, 0);
        return padded;
    }

    private static float[] FixtureProbabilities() =>
        [.. Fixture.GetProperty("probabilities").EnumerateArray().Select(p => p.GetSingle())];

    private static VadOptions Options(string name)
    {
        var o = Fixture.GetProperty("options").GetProperty(name);
        return new VadOptions
        {
            Threshold = o.GetProperty("threshold").GetDouble(),
            NegThreshold = o.GetProperty("neg_threshold").ValueKind == JsonValueKind.Null ? null : o.GetProperty("neg_threshold").GetDouble(),
            MinSpeechDurationMs = o.GetProperty("min_speech_duration_ms").GetInt32(),
            MaxSpeechDurationS = o.GetProperty("max_speech_duration_s").GetDouble(),
            MinSilenceDurationMs = o.GetProperty("min_silence_duration_ms").GetInt32(),
            SpeechPadMs = o.GetProperty("speech_pad_ms").GetInt32(),
        };
    }

    private static List<SpeechChunk> Expected(string name) =>
        [.. Fixture.GetProperty("timestamps").GetProperty(name).EnumerateArray()
            .Select(c => new SpeechChunk(c.GetProperty("start").GetInt32(), c.GetProperty("end").GetInt32()))];

    public static TheoryData<string> OptionSets() => new(Fixture.GetProperty("options").EnumerateObject().Select(p => p.Name));

    [Fact]
    public void ProbabilitiesMatchFasterWhisper()
    {
        var expected = FixtureProbabilities();
        var actual = SileroVad.Probabilities(Padded(Audio()));

        Assert.Equal(expected.Length, actual.Length);
        var maxDiff = expected.Zip(actual, (e, a) => Math.Abs(e - a)).Max();
        Assert.True(maxDiff <= 1e-5, $"max difference {maxDiff}");
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void SpeechTimestampsFromProbabilitiesMatchFasterWhisper(string optionSet) =>
        Assert.Equal(Expected(optionSet), SpeechTimestamps.FromProbabilities(FixtureProbabilities(), Audio().Length, Options(optionSet)));

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void SpeechChunksFromAudioMatchFasterWhisper(string optionSet) =>
        Assert.Equal(Expected(optionSet), SileroVad.SpeechChunks(Audio(), Options(optionSet)));

    [Fact]
    public void CollectJoinsTheSpeech()
    {
        var audio = Audio();
        var chunks = Expected("default");
        var kept = SpeechTimestamps.Collect(audio, chunks);

        Assert.Equal(Fixture.GetProperty("collected_samples").GetInt32(), kept.Length);
        Assert.Equal(audio.AsSpan(chunks[1].Start, chunks[1].Length).ToArray(), kept[chunks[0].Length..]);
    }

    [Fact]
    public void TimestampMapMatchesFasterWhisper()
    {
        var map = new SpeechTimestampsMap(Expected("default"));
        foreach (var c in Fixture.GetProperty("restore").EnumerateArray())
        {
            var (time, isEnd) = (c.GetProperty("time").GetDouble(), c.GetProperty("is_end").GetBoolean());
            Assert.Equal(c.GetProperty("chunk").GetInt32(), map.ChunkIndex(time, isEnd));
            Assert.Equal(c.GetProperty("original").GetDouble(), map.OriginalTime(time, isEnd: isEnd));
        }
    }

    [Fact]
    public void OriginalTimesRoundLikePython()
    {
        var noSilence = new SpeechTimestampsMap([new SpeechChunk(0, int.MaxValue)]);
        foreach (var pair in Fixture.GetProperty("round2").EnumerateArray())
            Assert.Equal(pair[1].GetDouble(), noSilence.OriginalTime(pair[0].GetDouble()));
    }

    [Fact]
    public void RestoreMovesWordsByTheirMiddleAndSegmentsByTheirEnds()
    {
        // 1 s of silence, 1 s of speech, 2 s of silence, 1 s of speech: joined, the second chunk starts at 1.0 s
        List<SpeechChunk> chunks = [new(16000, 32000), new(64000, 80000)];
        var straddling = new TimedWord(" across", 0.9, 1.2, 0.8); // middle 1.05 s: in the second chunk
        var inFirst = new TimedWord(" first", 0.2, 0.5, 0.9);
        Segment withWords = new(0, 0, 0.2, 1.2, " first across", [], 0, -0.2, 1.1, 0.01, [inFirst, straddling]);
        Segment withoutWords = new(1, 0, 0.5, 1.0, " end", [], 0, -0.2, 1.1, 0.01, null);

        var restored = SpeechTimestamps.Restore([withWords, withoutWords], chunks).ToList();

        Assert.Equal([(1.2, 1.5), (3.9, 4.2)], restored[0].Words!.Select(w => (w.Start, w.End)));
        Assert.Equal((1.2, 4.2), (restored[0].Start, restored[0].End));
        Assert.Equal(0.9, straddling.Start); // the transcriber's own words are left alone
        // a segment end exactly at a chunk end stays in that chunk
        Assert.Equal((1.5, 2.0), (restored[1].Start, restored[1].End));
    }
}
