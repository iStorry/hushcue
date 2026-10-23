using Hushcue.Core.Engine;
using Hushcue.Core.Media;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// The outside work a <see cref="SubtitleJob"/> does, replaceable so tests can fake the model and the media steps
/// (loading the model, embedding, recycling) and benchmarks can feed in features.
/// </summary>
internal sealed class JobSteps
{
    /// <summary>The real steps, with the model kept loaded between runs.</summary>
    public static JobSteps Default { get; } = new();

    public Func<int> CudaDeviceCount { get; init; } = WhisperModel.CudaDeviceCount;

    /// <summary>A model's folder, or null when it is not downloaded.</summary>
    public Func<string, string?> FindModel { get; init; } = Models.Find;

    /// <summary>Download a model (name, bytes done and total) and return its folder.</summary>
    public Func<string, Action<long, long>, CancellationToken, Task<string>> DownloadModel { get; init; } = ModelDownload.DownloadAsync;

    /// <summary>Load the model in a folder on "cuda" or "cpu". A transcriber that is IDisposable is disposed on unload.</summary>
    public Func<string, string, ITranscriber> LoadModel { get; init; } = LoadWhisper;

    /// <summary>A file's audio as 16 kHz mono samples (path, enhance).</summary>
    public Func<string, bool, CancellationToken, Task<float[]>> ReadAudio { get; init; } = Audio.ReadAsync;

    /// <summary>Part of a file's audio as 16 kHz mono samples (path, from, length in seconds, enhance).</summary>
    public Func<string, double, double, bool, CancellationToken, Task<float[]>> ReadAudioPart { get; init; } = Audio.ReadPartAsync;

    /// <summary>Silero's speech probability for each 512-sample window of 16 kHz audio, for <see cref="UnheardCues"/>.</summary>
    public Func<float[], CancellationToken, float[]> SpeechProbabilities { get; init; } = SileroVad.SpeechProbabilities;

    /// <summary>Put the subtitles into the video (video, srt, output folder, mode, progress) and return the new file.</summary>
    public Func<string, string, string?, EmbedMode, Action<double>, CancellationToken, Task<string>> Embed { get; init; } =
        (video, srt, outDir, mode, onFraction, cancel) => Media.Embed.SubtitlesAsync(video, srt, outDir, mode, onFraction, cancel: cancel);

    /// <summary>Move a file to the Recycle Bin; false if it could not.</summary>
    public Func<string, bool> Recycle { get; init; } = RecycleBin.Send;

    /// <summary>The loaded model, kept between runs.</summary>
    public ModelCache Cache { get; } = new();

    private static ITranscriber LoadWhisper(string path, string device) => LoadWhisper(path, device, null);

    /// <summary>The CTranslate2 model in float16 on the GPU or int8 on the CPU.</summary>
    /// <param name="featureSource">see <see cref="Transcriber.FeatureSource"/></param>
    public static ITranscriber LoadWhisper(string path, string device, Func<float[], (float[], int)>? featureSource) =>
        new LoadedWhisper(WhisperModel.Load(path, device, device == "cuda" ? "float16" : "int8"), featureSource);

    private sealed class LoadedWhisper(WhisperModel model, Func<float[], (float[], int)>? featureSource) : ITranscriber, IDisposable
    {
        private readonly Transcriber _transcriber = new(model) { FeatureSource = featureSource };

        public (IEnumerable<Segment> Segments, TranscriptionInfo Info) Transcribe(float[] audio, TranscribeOptions options,
                                                                                  CancellationToken cancel = default) =>
            _transcriber.Transcribe(audio, options, cancel);

        public void Dispose() => model.Dispose();
    }
}

/// <summary>One loaded model, reused while the folder and device stay the same.</summary>
internal sealed class ModelCache
{
    private readonly Lock _lock = new();
    private (string Path, string Device, ITranscriber Model)? _loaded;

    /// <param name="onLoading">called before a model has to be loaded</param>
    public ITranscriber Get(string path, string device, Func<string, string, ITranscriber> load, Action onLoading)
    {
        lock (_lock)
        {
            if (_loaded is { } loaded && loaded.Path == path && loaded.Device == device)
                return loaded.Model;
            Unload();
            onLoading();
            var model = load(path, device);
            _loaded = (path, device, model);
            return model;
        }
    }

    /// <summary>The device the model in <paramref name="path"/> is loaded on, or null when it is not loaded.</summary>
    public string? DeviceOf(string path)
    {
        lock (_lock)
            return _loaded is { } loaded && loaded.Path == path ? loaded.Device : null;
    }

    /// <summary>Release the loaded model, if any.</summary>
    public void Unload()
    {
        lock (_lock)
        {
            (_loaded?.Model as IDisposable)?.Dispose();
            _loaded = null;
        }
    }
}
