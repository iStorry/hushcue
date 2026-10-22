using Microsoft.ML.OnnxRuntime;

namespace Hushcue.Core.Engine;

/// <summary>
/// faster-whisper 1.2.1's SileroVADModel: the Silero VAD v6 network (embedded in this assembly) on one CPU thread, over
/// 512-sample windows that each carry the 64 samples before them as context, with the LSTM state passed from one batch
/// of 10 000 windows to the next. The session is created on first use and kept, as faster-whisper caches it.
/// </summary>
public static class SileroVad
{
    public const int WindowSamples = 512;
    private const int ContextSamples = 64;
    private const int RowSamples = ContextSamples + WindowSamples;
    private const int BatchWindows = 10000;
    private const int StateSize = 128;
    private const string ModelResource = "silero_vad_v6.onnx";
    private static readonly string[] InputNames = ["input", "h", "c"];
    private static readonly string[] OutputNames = ["speech_probs", "hn", "cn"];
    private static readonly Lazy<InferenceSession> Session = new(CreateSession);

    /// <summary>get_speech_timestamps: where the 16 kHz <paramref name="audio"/> contains speech.</summary>
    public static List<SpeechChunk> SpeechChunks(float[] audio, VadOptions options, CancellationToken cancel = default)
    {
        var padded = new float[audio.Length + WindowSamples - audio.Length % WindowSamples];
        audio.CopyTo(padded, 0);
        return SpeechTimestamps.FromProbabilities(Probabilities(padded, cancel), audio.Length, options);
    }

    /// <summary>The speech probability of each window of <paramref name="padded"/>, whose length is a multiple of 512.</summary>
    public static float[] Probabilities(float[] padded, CancellationToken cancel = default)
    {
        if (padded.Length % WindowSamples != 0)
            throw new ArgumentException($"length {padded.Length} is not a multiple of {WindowSamples}", nameof(padded));
        var windows = padded.Length / WindowSamples;
        var probabilities = new float[windows];
        var h = new float[StateSize];
        var c = new float[StateSize];
        using var runOptions = new RunOptions();
        for (var first = 0; first < windows; first += BatchWindows)
        {
            cancel.ThrowIfCancellationRequested();
            var count = Math.Min(BatchWindows, windows - first);
            var input = BatchInput(padded, first, count, last: first + count == windows);
            using var inputValue = OrtValue.CreateTensorValueFromMemory(input, [count, RowSamples]);
            using var hValue = OrtValue.CreateTensorValueFromMemory(h, [1, 1, StateSize]);
            using var cValue = OrtValue.CreateTensorValueFromMemory(c, [1, 1, StateSize]);
            using var outputs = Session.Value.Run(runOptions, InputNames, [inputValue, hValue, cValue], OutputNames);
            outputs[0].GetTensorDataAsSpan<float>().CopyTo(probabilities.AsSpan(first, count));
            h = outputs[1].GetTensorDataAsSpan<float>().ToArray();
            c = outputs[2].GetTensorDataAsSpan<float>().ToArray();
        }
        return probabilities;
    }

    /// <summary>Rows of context + window. The first window's context is silence, and the last window ends in 64 zeros:
    /// faster-whisper clears them through a numpy view while building the contexts.</summary>
    private static float[] BatchInput(float[] padded, int first, int count, bool last)
    {
        var input = new float[count * RowSamples];
        for (var row = 0; row < count; row++)
        {
            var window = first + row;
            var target = input.AsSpan(row * RowSamples, RowSamples);
            if (window > 0)
                padded.AsSpan(window * WindowSamples - ContextSamples, ContextSamples).CopyTo(target);
            padded.AsSpan(window * WindowSamples, WindowSamples).CopyTo(target[ContextSamples..]);
        }
        if (last)
            input.AsSpan(input.Length - ContextSamples).Clear();
        return input;
    }

    private static InferenceSession CreateSession()
    {
        using var stream = typeof(SileroVad).Assembly.GetManifestResourceStream(ModelResource)
                           ?? throw new InvalidOperationException($"{ModelResource} is not embedded in Hushcue.Core");
        using var model = new MemoryStream();
        stream.CopyTo(model);
        using var options = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            EnableCpuMemArena = false,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL,
        };
        return new InferenceSession(model.ToArray(), options);
    }
}
