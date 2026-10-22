using System.Runtime.InteropServices;
using System.Text.Json;
using Hushcue.Core.Engine;

namespace Hushcue.Core.Tests;

/// <summary>A test that needs large-v3-turbo's tokenizer.json (from the Hugging Face cache or HUSHCUE_MODEL).</summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        if (EngineTests.ModelFolder() is null)
            Skip = "needs the faster-whisper-large-v3-turbo model (Hugging Face cache or HUSHCUE_MODEL)";
    }
}

/// <summary>
/// The C# engine against faster-whisper's own results (fixtures from tools/bench/make_engine_fixtures.py).
/// Whole transcriptions can be checked against faster-whisper with tools/bench/fw_reference.py and compare_words.py.
/// </summary>
public class EngineTests
{
    private static string Golden(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    public static string? ModelFolder()
    {
        if (Environment.GetEnvironmentVariable("HUSHCUE_MODEL") is { Length: > 0 } configured && Directory.Exists(configured))
            return configured;
        var snapshots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache",
                                     "huggingface", "hub", "models--mobiuslabsgmbh--faster-whisper-large-v3-turbo", "snapshots");
        return Directory.Exists(snapshots)
            ? Directory.EnumerateDirectories(snapshots).FirstOrDefault(d => File.Exists(Path.Combine(d, "tokenizer.json")))
            : null;
    }

    [Fact]
    public void MelSpectrogramMatchesFasterWhisper()
    {
        var pcm = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Golden("mel-input.s16")));
        var audio = new float[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
            audio[i] = pcm[i] / 32768.0f;
        var expected = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Golden("mel-128.f32"))).ToArray();

        var (actual, frames) = new MelSpectrogram(128).Compute(audio);

        Assert.Equal(audio.Length / 160 + 1, frames);
        Assert.Equal(expected.Length, actual.Length);
        var maxDiff = expected.Zip(actual, (e, a) => Math.Abs(e - a)).Max();
        var exact = expected.Zip(actual).Count(p => p.First == p.Second) / (double)expected.Length;
        Assert.True(maxDiff <= 2.5e-7, $"max difference {maxDiff}"); // at most a float32 step or two
        Assert.True(exact >= 0.95, $"only {exact:P1} bit-identical");
    }

    [Fact]
    public void WindowIsZeroPaddedTo3000Frames()
    {
        var mel = new MelSpectrogram(2);
        float[] data = [1, 2, 3, 4, 5, 6]; // 2 bands x 3 frames
        var window = mel.Window3000(data, 3, 1, 2);
        Assert.Equal(2 * 3000, window.Length);
        Assert.Equal([2f, 3f, 0f], window[..3]);
        Assert.Equal([5f, 6f, 0f], window[3000..3003]);
    }

    private static JsonElement TokenizerFixture() =>
        JsonDocument.Parse(File.ReadAllText(Golden("tokenizer.json"))).RootElement;

    private static WhisperTokenizer Tokenizer() =>
        new(Path.Combine(ModelFolder()!, "tokenizer.json"), multilingual: true, "transcribe", "en");

    private static int[] Ints(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetInt32())];

    [ModelFact]
    public void SpecialTokensMatchFasterWhisper()
    {
        var special = TokenizerFixture().GetProperty("special");
        var tok = Tokenizer();
        Assert.Equal(special.GetProperty("sot").GetInt32(), tok.Sot);
        Assert.Equal(special.GetProperty("eot").GetInt32(), tok.Eot);
        Assert.Equal(special.GetProperty("sot_prev").GetInt32(), tok.SotPrev);
        Assert.Equal(special.GetProperty("no_timestamps").GetInt32(), tok.NoTimestamps);
        Assert.Equal(special.GetProperty("no_speech").GetInt32(), tok.NoSpeech);
        Assert.Equal(special.GetProperty("timestamp_begin").GetInt32(), tok.TimestampBegin);
        Assert.Equal(Ints(special.GetProperty("sot_sequence")), tok.SotSequence);
    }

    [ModelFact]
    public void EncodeAndDecodeMatchFasterWhisper()
    {
        var fixture = TokenizerFixture();
        var tok = Tokenizer();
        foreach (var case_ in fixture.GetProperty("encodings").EnumerateObject())
        {
            Assert.Equal(Ints(case_.Value), tok.Encode(case_.Name));
            Assert.Equal(fixture.GetProperty("decodes").GetProperty(case_.Name).GetString(), tok.Decode(Ints(case_.Value)));
        }
        // half of a multi-byte character decodes to U+FFFD, as in the tokenizers library
        Assert.Equal(fixture.GetProperty("partial_decode").GetString(), tok.Decode(tok.Encode(" 日本")[..1]));
    }

    [ModelFact]
    public void NonSpeechTokensMatchFasterWhisper() =>
        Assert.Equal(Ints(TokenizerFixture().GetProperty("non_speech_tokens")), Tokenizer().NonSpeechTokens());

    [ModelFact]
    public void WordSplitMatchesFasterWhisper()
    {
        var split = TokenizerFixture().GetProperty("split");
        var (words, wordTokens) = Tokenizer().SplitToWordTokens(Ints(split.GetProperty("tokens")));
        Assert.Equal(split.GetProperty("words").EnumerateArray().Select(w => w.GetString()), words);
        Assert.Equal(split.GetProperty("word_tokens").EnumerateArray().Select(Ints), wordTokens.Select(t => t.ToArray()));
    }
}
