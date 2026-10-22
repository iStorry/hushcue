using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Hushcue.Core.Engine;
using Hushcue.Core.Media;

namespace Hushcue.Cli;

/// <summary>
/// Transcribe files with the CTranslate2 engine, decoding the way the Balanced preset does (beam 5, temperature 0,
/// no previous-text context, English, word timestamps, no VAD unless --vad options are given), and record the speed.
/// Writes NAME.raw.srt (one cue per segment, no clean-up), NAME.words.json and metrics.json into the output folder,
/// for comparing with faster-whisper (tools/bench/compare_words.py). --features DIR decodes faster-whisper's recorded features
/// (<see cref="FeatureFiles"/>) instead of the engine's own, with or without VAD.
/// </summary>
internal static class TranscribeCommand
{
    public const string Usage =
        """
        usage: Hushcue.Cli transcribe --model DIR --out DIR [--device cuda|cpu] [--compute-type TYPE] [--features DIR]
                                      [--language CODE|auto] [--prompt TEXT] [--repetition-penalty P]
                                      [--hallucination-silence SECONDS]
                                      [--vad] [--vad-threshold T] [--vad-min-silence MS] [--vad-speech-pad MS] FILE...
        """;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> RunAsync(string[] args)
    {
        string? model = null, outDir = null, device = null, computeType = null, featuresDir = null, prompt = null;
        string? language = "en";
        var repetitionPenalty = 1.0f;
        double? hallucinationSilence = null;
        VadOptions? vad = null;
        var files = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length: model = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--device" when i + 1 < args.Length: device = args[++i]; break;
                case "--compute-type" when i + 1 < args.Length: computeType = args[++i]; break;
                case "--features" when i + 1 < args.Length: featuresDir = args[++i]; break;
                case "--language" when i + 1 < args.Length: language = args[++i] == "auto" ? null : args[i]; break;
                case "--prompt" when i + 1 < args.Length: prompt = args[++i]; break;
                case "--repetition-penalty" when i + 1 < args.Length: repetitionPenalty = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--hallucination-silence" when i + 1 < args.Length: hallucinationSilence = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--vad": vad ??= new VadOptions(); break;
                case "--vad-threshold" when i + 1 < args.Length: vad = (vad ?? new VadOptions()) with { Threshold = double.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--vad-min-silence" when i + 1 < args.Length: vad = (vad ?? new VadOptions()) with { MinSilenceDurationMs = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--vad-speech-pad" when i + 1 < args.Length: vad = (vad ?? new VadOptions()) with { SpeechPadMs = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                default: files.Add(args[i]); break;
            }
        }
        if (model is null || outDir is null || files.Count == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        device ??= WhisperModel.CudaDeviceCount() > 0 ? "cuda" : "cpu";
        computeType ??= device == "cuda" ? "float16" : "int8";
        Directory.CreateDirectory(outDir);

        var load = Stopwatch.StartNew();
        using var whisper = WhisperModel.Load(model, device, computeType);
        var transcriber = new Transcriber(whisper)
        {
            FeatureSource = featuresDir is null ? null : FeatureFiles.Source(featuresDir, whisper.NMels),
        };
        load.Stop();
        Console.WriteLine($"Loaded {Path.GetFileName(model.TrimEnd('\\', '/'))} on {device} ({computeType}) in {load.Elapsed.TotalSeconds:F1} s");

        var options = new TranscribeOptions
        {
            Language = language,
            InitialPrompt = prompt,
            RepetitionPenalty = repetitionPenalty,
            HallucinationSilenceThreshold = hallucinationSilence,
            Vad = vad,
            BeamSize = 5,
            Temperatures = [0.0f],
            ConditionOnPreviousText = false,
            WordTimestamps = true,
            NoSpeechThreshold = 0.6,
            LogProbThreshold = -1.0,
        };
        var clips = new Dictionary<string, object>();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var audio = await Audio.ReadAsync(file);
            var audioSeconds = audio.Length / 16000.0;
            var run = Stopwatch.StartNew();
            var (segments, info) = transcriber.Transcribe(audio, options);
            var list = segments.ToList();
            run.Stop();
            if (language is null)
                Console.WriteLine($"{name}: detected language {info.Language} ({info.LanguageProbability:F4})");

            var words = list.SelectMany(s => s.Words ?? []).ToList();
            await File.WriteAllTextAsync(Path.Combine(outDir, name + ".raw.srt"), Srt(list), Utf8);
            await File.WriteAllTextAsync(Path.Combine(outDir, name + ".words.json"),
                JsonSerializer.Serialize(words.Select(w => new { word = w.Text, start = w.Start, end = w.End, probability = w.Probability })), Utf8);
            clips[name] = new Dictionary<string, object>
            {
                ["audio_seconds"] = audioSeconds,
                ["raw_seconds"] = run.Elapsed.TotalSeconds,
                ["raw_realtime"] = audioSeconds / run.Elapsed.TotalSeconds,
                ["raw_peak_ws_mb"] = 0.0,
                ["segments"] = list.Count,
                ["words"] = words.Count,
            };
            Console.WriteLine($"{name}: {audioSeconds / run.Elapsed.TotalSeconds:F0}x realtime, {words.Count} words, {run.Elapsed.TotalSeconds:F2} s"
                              + $" (so far: {string.Join(", ", whisper.Timings.Select(t => $"{t.Key} {t.Value.TotalSeconds:F2} s"))})");
        }
        var metrics = new Dictionary<string, object>
        {
            ["engine"] = "ctranslate2",
            ["model"] = Path.GetFileName(model.TrimEnd('\\', '/')),
            ["device"] = device,
            ["compute_type"] = computeType,
            ["load_seconds"] = load.Elapsed.TotalSeconds,
            ["after_load_ws_mb"] = 0.0,
            ["clips"] = clips,
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, "metrics.json"),
            JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>One cue per segment, no clean-up.</summary>
    private static string Srt(IEnumerable<Segment> segments) =>
        Hushcue.Core.Subtitles.Srt.Write(segments.Where(s => s.Text.Trim().Length > 0)
                                                  .Select(s => new Hushcue.Core.Subtitles.Cue(s.Start, s.End, s.Text.Trim())));
}
