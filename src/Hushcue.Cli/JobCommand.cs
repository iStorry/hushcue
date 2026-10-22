using System.Diagnostics;
using Hushcue.Core.Engine;
using Hushcue.Core.Pipeline;

namespace Hushcue.Cli;

/// <summary>
/// Run the app's subtitle job (<see cref="SubtitleJob"/>: decode, loop recovery, clean-up) on files and write
/// OUT/NAME.srt, overwriting it. With --features DIR the engine decodes faster-whisper's recorded features
/// (tools/bench/subgen_job.py) instead of its own, so the result can be compared with SubGen's byte for byte.
/// </summary>
internal static class JobCommand
{
    public const string Usage =
        """
        usage: Hushcue.Cli job --out DIR [--preset NAME] [--model DIR] [--device cuda|cpu] [--features DIR]
                               [--language CODE|auto] [--prompt TEXT] [--skip-silence] [--no-recover-loops] FILE...
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        string? outDir = null, modelDir = null, device = null, featuresDir = null;
        var settings = new JobSettings();
        var files = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--preset" when i + 1 < args.Length: settings = settings with { Preset = args[++i] }; break;
                case "--model" when i + 1 < args.Length: modelDir = args[++i]; break;
                case "--device" when i + 1 < args.Length: device = args[++i]; break;
                case "--features" when i + 1 < args.Length: featuresDir = args[++i]; break;
                case "--language" when i + 1 < args.Length: settings = settings with { Language = args[++i] == "auto" ? null : args[i] }; break;
                case "--prompt" when i + 1 < args.Length: settings = settings with { Vocabulary = args[++i] }; break;
                case "--skip-silence": settings = settings with { SkipSilence = true }; break;
                case "--no-recover-loops": settings = settings with { RecoverLoops = false }; break;
                default: files.Add(args[i]); break;
            }
        }
        if (outDir is null || files.Count == 0 || Presets.All.All(p => p.Name != settings.Preset))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        Directory.CreateDirectory(outDir);
        foreach (var file in files)
            File.Delete(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".srt"));

        var steps = new JobSteps
        {
            CudaDeviceCount = () => device == "cpu" ? 0 : WhisperModel.CudaDeviceCount(),
            FindModel = name => modelDir ?? Models.Find(name),
            LoadModel = (path, dev) => JobSteps.LoadWhisper(path, dev,
                featuresDir is null ? null : FeatureFiles.Source(featuresDir, WhisperModel.FeatureSize(path))),
        };
        var clock = Stopwatch.StartNew();
        var outputs = await SubtitleJob.RunAsync(files, outDir, settings, (_, _) => { }, Console.WriteLine, null, steps,
                                                 CancellationToken.None);
        Console.WriteLine($"{outputs.Count} file(s) in {clock.Elapsed.TotalSeconds:F1} s");
        steps.Cache.Unload();
        return 0;
    }
}
