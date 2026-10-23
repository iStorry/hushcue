using System.Runtime.InteropServices;
using System.Text.Json;

namespace Hushcue.Core.Engine;

/// <summary>Something CTranslate2 could not do (load the model, run on the GPU, ...).</summary>
public sealed class EngineException(string message) : Exception(message);

public sealed record GenerationResult(List<int[]> SequencesIds, float[] Scores, float NoSpeechProb);

public sealed record AlignmentResult((int TextIndex, int TimeIndex)[] Alignments, float[] TextTokenProbs);

/// <summary>Generation settings, as faster-whisper passes them to CTranslate2's Whisper.generate.</summary>
public sealed record GenerateSettings
{
    public int BeamSize { get; init; } = 5;
    public float Patience { get; init; } = 1;
    public float LengthPenalty { get; init; } = 1;
    public float RepetitionPenalty { get; init; } = 1;
    public int NoRepeatNgramSize { get; init; }
    public int MaxLength { get; init; } = 448;
    public int SamplingTopK { get; init; } = 1;
    public float SamplingTemperature { get; init; } = 1;
    public int NumHypotheses { get; init; } = 1;
    public bool ReturnScores { get; init; }
    public bool ReturnNoSpeechProb { get; init; }
    public int MaxInitialTimestampIndex { get; init; } = 50;
    public bool SuppressBlank { get; init; } = true;
    public IReadOnlyList<int>? SuppressTokens { get; init; }
}

/// <summary>
/// A CTranslate2 Whisper model (the same engine and model files faster-whisper uses), loaded through the native
/// hushcue_ct2 bridge. Not thread-safe: use one transcription at a time.
/// </summary>
public sealed class WhisperModel : IDisposable
{
    private IntPtr _handle;

    /// <summary>Time spent in each native call since load, for profiling.</summary>
    public Dictionary<string, TimeSpan> Timings { get; } = new() { ["encode"] = default, ["generate"] = default, ["align"] = default };

    private void Track(string name, long started) =>
        Timings[name] += System.Diagnostics.Stopwatch.GetElapsedTime(started);

    static WhisperModel() => RuntimeLibraries.Register();

    private WhisperModel(IntPtr handle, string path, string device)
    {
        _handle = handle;
        Path = path;
        Device = device;
        IsMultilingual = Native.IsMultilingual(handle) == 1;
        NMels = Native.NMels(handle);
    }

    public string Path { get; }
    public string Device { get; }
    public bool IsMultilingual { get; }
    public int NMels { get; }

    public static int CudaDeviceCount()
    {
        RuntimeLibraries.Register();
        return Native.CudaDeviceCount();
    }

    /// <summary>Load a converted model folder (model.bin, config.json, tokenizer.json, preprocessor_config.json).</summary>
    /// <param name="device">"cuda" or "cpu".</param>
    /// <param name="computeType">"float16" on the GPU and "int8" on the CPU.</param>
    public static WhisperModel Load(string path, string device, string computeType, int deviceIndex = 0,
                                    int interThreads = 1, int intraThreads = 0)
    {
        var handle = Native.Load(path, device, deviceIndex, computeType, interThreads, intraThreads);
        if (handle == IntPtr.Zero)
            throw new EngineException(Native.ErrorMessage());
        return new WhisperModel(handle, path, device);
    }

    /// <summary>The n_mels a model folder expects, from preprocessor_config.json (128 for large-v3, else 80).</summary>
    public static int FeatureSize(string path)
    {
        var config = System.IO.Path.Combine(path, "preprocessor_config.json");
        if (!File.Exists(config))
            return 80;
        using var json = JsonDocument.Parse(File.ReadAllBytes(config));
        return json.RootElement.TryGetProperty("feature_size", out var size) ? size.GetInt32() : 80;
    }

    /// <summary>Run the encoder on one 3000-frame window; the result stays on the device for Generate / Align.</summary>
    public unsafe EncodedWindow Encode(float[] window)
    {
        if (window.Length != NMels * MelSpectrogram.FramesPerWindow)
            throw new ArgumentException("window must be NMels x 3000 values", nameof(window));
        fixed (float* features = window)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var encoded = Native.Encode(Handle, features, NMels, MelSpectrogram.FramesPerWindow);
            Track("encode", started);
            if (encoded == IntPtr.Zero)
                throw new EngineException(Native.ErrorMessage());
            return new EncodedWindow(encoded);
        }
    }

    public unsafe GenerationResult Generate(EncodedWindow encoded, IReadOnlyList<int> prompt, GenerateSettings settings)
    {
        var promptArray = prompt.ToArray();
        var suppress = settings.SuppressTokens?.ToArray();
        fixed (int* promptPtr = promptArray)
        fixed (int* suppressPtr = suppress)
        {
            var options = new Native.GenerateOptions
            {
                BeamSize = settings.BeamSize,
                Patience = settings.Patience,
                LengthPenalty = settings.LengthPenalty,
                RepetitionPenalty = settings.RepetitionPenalty,
                NoRepeatNgramSize = settings.NoRepeatNgramSize,
                MaxLength = settings.MaxLength,
                SamplingTopK = settings.SamplingTopK,
                SamplingTemperature = settings.SamplingTemperature,
                NumHypotheses = settings.NumHypotheses,
                ReturnScores = settings.ReturnScores ? 1 : 0,
                ReturnNoSpeechProb = settings.ReturnNoSpeechProb ? 1 : 0,
                MaxInitialTimestampIndex = settings.MaxInitialTimestampIndex,
                SuppressBlank = settings.SuppressBlank ? 1 : 0,
                SuppressTokens = suppress is null ? null : suppressPtr,
                SuppressTokensCount = suppress?.Length ?? 0,
            };
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = Native.Generate(Handle, encoded.Handle, promptPtr, promptArray.Length, &options);
            Track("generate", started);
            if (result == null)
                throw new EngineException(Native.ErrorMessage());
            try
            {
                var sequences = new List<int[]>();
                var offset = 0;
                for (var h = 0; h < result->Hypotheses; h++)
                {
                    var length = result->Lengths[h];
                    sequences.Add(new ReadOnlySpan<int>(result->Ids + offset, length).ToArray());
                    offset += length;
                }
                var scores = new ReadOnlySpan<float>(result->Scores, settings.ReturnScores ? result->Hypotheses : 0).ToArray();
                return new GenerationResult(sequences, scores, result->NoSpeechProb);
            }
            finally
            {
                Native.GenerationFree(result);
            }
        }
    }

    public unsafe AlignmentResult Align(EncodedWindow encoded, IReadOnlyList<int> startSequence,
                                        IReadOnlyList<int> textTokens, int numFrames, int medianFilterWidth = 7)
    {
        var start = startSequence.ToArray();
        var text = textTokens.ToArray();
        fixed (int* startPtr = start)
        fixed (int* textPtr = text)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = Native.Align(Handle, encoded.Handle, startPtr, start.Length, textPtr, text.Length, numFrames,
                                      medianFilterWidth);
            Track("align", started);
            if (result == null)
                throw new EngineException(Native.ErrorMessage());
            try
            {
                var pairs = new (int, int)[result->Count];
                for (var i = 0; i < pairs.Length; i++)
                    pairs[i] = (result->TextIndices[i], result->TimeIndices[i]);
                var probs = new ReadOnlySpan<float>(result->TextTokenProbs, result->ProbsCount).ToArray();
                return new AlignmentResult(pairs, probs);
            }
            finally
            {
                Native.AlignmentFree(result);
            }
        }
    }

    /// <summary>Language tokens ("&lt;|en|&gt;") with probabilities, most likely first.</summary>
    public unsafe List<(string Token, float Probability)> DetectLanguage(EncodedWindow encoded)
    {
        var result = Native.DetectLanguage(Handle, encoded.Handle);
        if (result == null)
            throw new EngineException(Native.ErrorMessage());
        try
        {
            var languages = new List<(string, float)>(result->Count);
            for (var i = 0; i < result->Count; i++)
                languages.Add((Marshal.PtrToStringUTF8((IntPtr)result->Tokens[i]) ?? "", result->Probs[i]));
            return languages;
        }
        finally
        {
            Native.LanguagesFree(result);
        }
    }

    private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(WhisperModel));

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            Native.Free(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>Encoder output for one window, kept on the device. Dispose it when the window is done.</summary>
public sealed class EncodedWindow : IDisposable
{
    internal EncodedWindow(IntPtr handle) => Handle = handle;

    internal IntPtr Handle { get; private set; }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.EncodedFree(Handle);
            Handle = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Where the native libraries come from: hushcue_ct2.dll next to the app; CTranslate2 from the runtime folder next to
/// the app (or %LOCALAPPDATA%\Hushcue\runtime on a development machine); cuBLAS from %LOCALAPPDATA%\Hushcue\runtime,
/// where <see cref="Pipeline.RuntimeDownload"/> puts it. Both folders go on the DLL search path, the app's first.
/// </summary>
public static class RuntimeLibraries
{
    private static int _registered;

    /// <summary>The folder ctranslate2.dll loads from, or null if there is none.</summary>
    public static string? Folder { get; private set; }

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;
        string[] candidates = [Pipeline.RuntimeDownload.BundledFolder, Pipeline.RuntimeDownload.Folder];
        Folder = candidates.FirstOrDefault(dir => File.Exists(System.IO.Path.Combine(dir, "ctranslate2.dll")));
        // both, even before the GPU libraries are downloaded: CTranslate2 only loads cuBLAS when it first needs it
        Environment.SetEnvironmentVariable("PATH", string.Join(System.IO.Path.PathSeparator, [.. candidates, Environment.GetEnvironmentVariable("PATH")]));
    }
}
