using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Hushcue.Cli;

/// <summary>
/// Phase 1 engine spike: transcribe files with Whisper.net, decoding the way SubGen's Balanced preset does
/// (beam 5, temperature 0 with no fallback, no previous-text context, English), and record speed and memory.
/// Writes NAME.raw.srt (one cue per segment, no clean-up), NAME.words.json and metrics.json into the output folder.
/// </summary>
internal static class TranscribeCommand
{
    public const string Usage =
        "usage: Hushcue.Cli transcribe --model FILE --out DIR [--runtime cuda12|cuda|vulkan|cpu] [--beam N]\n" +
        "                              [--dtw] [--flash] [--cuda-dir DIR] FILE...";

    private sealed record Options(string Model, string OutDir, RuntimeLibrary Runtime, int Beam, bool Dtw, bool Flash,
                                  string CudaDir, List<string> Files);

    private sealed record Word(string Text, double Start, double End);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> RunAsync(string[] args)
    {
        var options = Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.Runtime == RuntimeLibrary.Cuda12 && Directory.Exists(options.CudaDir))
        {
            // cudart64_12 / cublas64_12 are not shipped by Whisper.net; the loader finds them through PATH.
            Environment.SetEnvironmentVariable("PATH", options.CudaDir + ";" + Environment.GetEnvironmentVariable("PATH"));
        }
        RuntimeOptions.RuntimeLibraryOrder = [options.Runtime];
        Directory.CreateDirectory(options.OutDir);

        var metrics = new Dictionary<string, object?>
        {
            ["engine"] = "whisper.net",
            ["model"] = Path.GetFileName(options.Model),
            ["runtime"] = options.Runtime.ToString(),
            ["beam"] = options.Beam,
            ["dtw"] = options.Dtw,
            ["flash_attention"] = options.Flash,
            ["idle_ws_mb"] = WorkingSetMb(),
        };

        var load = Stopwatch.StartNew();
        using var factory = WhisperFactory.FromPath(options.Model, new WhisperFactoryOptions
        {
            UseGpu = options.Runtime != RuntimeLibrary.Cpu,
            UseFlashAttention = options.Flash,
            UseDtwTimeStamps = options.Dtw,
            HeadsPreset = options.Dtw ? WhisperAlignmentHeadsPreset.LargeV3Turbo : WhisperAlignmentHeadsPreset.None,
        });
        await using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithBeamSearchSamplingStrategy(b => b.WithBeamSize(options.Beam))
            .WithTemperature(0f)
            .WithTemperatureInc(0f) // no fallback to random sampling: same input, same output
            .WithNoContext()
            .WithNoSpeechThreshold(0.6f)
            .WithLogProbThreshold(-1f)
            .WithTokenTimestamps()
            .WithProbabilities()
            .Build();
        metrics["load_seconds"] = load.Elapsed.TotalSeconds;
        metrics["loaded_library"] = RuntimeOptions.LoadedLibrary?.ToString();
        metrics["after_load_ws_mb"] = WorkingSetMb();
        Console.WriteLine($"Loaded {Path.GetFileName(options.Model)} on {RuntimeOptions.LoadedLibrary} in {load.Elapsed.TotalSeconds:F1} s");

        var clips = new Dictionary<string, object>();
        foreach (var file in options.Files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var samples = await ReadAudioAsync(file);
            var audioSeconds = samples.Length / 16000.0;

            var segments = new List<SegmentData>();
            using var sampler = new PeakSampler();
            var run = Stopwatch.StartNew();
            await foreach (var segment in processor.ProcessAsync(samples))
                segments.Add(segment);
            run.Stop();
            var peak = sampler.Stop();

            var words = Words(segments, options.Dtw);
            await File.WriteAllTextAsync(Path.Combine(options.OutDir, name + ".raw.srt"), Srt(segments), Utf8);
            await File.WriteAllTextAsync(Path.Combine(options.OutDir, name + ".words.json"),
                JsonSerializer.Serialize(words.Select(w => new { word = w.Text, start = w.Start, end = w.End })), Utf8);

            clips[name] = new Dictionary<string, object>
            {
                ["audio_seconds"] = audioSeconds,
                ["raw_seconds"] = run.Elapsed.TotalSeconds,
                ["raw_realtime"] = audioSeconds / run.Elapsed.TotalSeconds,
                ["raw_peak_ws_mb"] = peak,
                ["segments"] = segments.Count,
                ["words"] = words.Count,
            };
            Console.WriteLine($"{name}: {audioSeconds / run.Elapsed.TotalSeconds:F0}x realtime, {words.Count} words, peak {peak:F0} MB");
        }
        metrics["clips"] = clips;
        await File.WriteAllTextAsync(Path.Combine(options.OutDir, "metrics.json"),
            JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static Options? Parse(string[] args)
    {
        string? model = null, outDir = null;
        var runtime = RuntimeLibrary.Cuda12;
        int beam = 5;
        bool dtw = false, flash = false;
        var cudaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hushcue", "cuda12");
        var files = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length: model = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--beam" when i + 1 < args.Length: beam = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cuda-dir" when i + 1 < args.Length: cudaDir = args[++i]; break;
                case "--runtime" when i + 1 < args.Length:
                    runtime = args[++i] switch
                    {
                        "cuda12" => RuntimeLibrary.Cuda12,
                        "cuda" => RuntimeLibrary.Cuda,
                        "vulkan" => RuntimeLibrary.Vulkan,
                        "cpu" => RuntimeLibrary.Cpu,
                        _ => throw new ArgumentException($"unknown runtime {args[i]}"),
                    };
                    break;
                case "--dtw": dtw = true; break;
                case "--flash": flash = true; break;
                default: files.Add(args[i]); break;
            }
        }
        return model is null || outDir is null || files.Count == 0
            ? null
            : new Options(model, outDir, runtime, beam, dtw, flash, cudaDir, files);
    }

    /// <summary>16 kHz mono float samples of any audio/video file, decoded by ffmpeg straight into memory.</summary>
    private static async Task<float[]> ReadAudioAsync(string file)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-i", file, "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1" })
            info.ArgumentList.Add(a);
        using var ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
        var errors = ffmpeg.StandardError.ReadToEndAsync();
        using var pcm = new MemoryStream();
        await ffmpeg.StandardOutput.BaseStream.CopyToAsync(pcm);
        await ffmpeg.WaitForExitAsync();
        if (ffmpeg.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not read {file}: {await errors}");
        var samples = new float[pcm.Length / sizeof(float)];
        Buffer.BlockCopy(pcm.GetBuffer(), 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    /// <summary>
    /// Words from Whisper tokens: a token starting with a space begins a new word. Special tokens ([_BEG_], [_TT_n])
    /// are skipped. Token times are in 10 ms units; with DTW each token's DTW time is used as its start instead.
    /// </summary>
    private static List<Word> Words(IEnumerable<SegmentData> segments, bool dtw)
    {
        var words = new List<Word>();
        var text = new StringBuilder();
        double start = 0, end = 0;
        void Flush()
        {
            if (text.ToString().Trim().Length > 0)
                words.Add(new Word(text.ToString(), start, Math.Max(end, start)));
            text.Clear();
        }
        foreach (var token in segments.SelectMany(s => s.Tokens))
        {
            var piece = token.Text ?? "";
            if (piece.Length == 0 || piece.StartsWith("[_", StringComparison.Ordinal) || piece.StartsWith("<|", StringComparison.Ordinal))
                continue;
            var tokenStart = (dtw && token.DtwTimestamp >= 0 ? token.DtwTimestamp : token.Start) / 100.0;
            if (piece.StartsWith(' ') || text.Length == 0)
            {
                Flush();
                start = tokenStart;
            }
            text.Append(piece);
            end = token.End / 100.0;
        }
        Flush();
        return words;
    }

    private static string Srt(IEnumerable<SegmentData> segments)
    {
        static string Ts(TimeSpan t) => t.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var s in segments)
        {
            var text = s.Text.Trim();
            if (text.Length == 0)
                continue;
            sb.Append(++n).Append('\n').Append(Ts(s.Start)).Append(" --> ").Append(Ts(s.End)).Append('\n')
              .Append(text).Append("\n\n");
        }
        return sb.ToString();
    }

    private static double WorkingSetMb()
    {
        using var self = Process.GetCurrentProcess();
        return self.WorkingSet64 / 1048576.0;
    }

    /// <summary>Highest working set seen until Stop (sampled every 50 ms).</summary>
    private sealed class PeakSampler : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        private double _peak = WorkingSetMb();

        public PeakSampler() => _task = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                _peak = Math.Max(_peak, WorkingSetMb());
                await Task.Delay(50);
            }
        });

        public double Stop()
        {
            _stop.Cancel();
            _task.Wait();
            return _peak;
        }

        public void Dispose() => _stop.Dispose();
    }
}
