using System.Diagnostics;
using System.Text.Json;
using Hushcue.Core.Engine;
using Hushcue.Core.Evaluation;
using Hushcue.Core.Pipeline;
using Hushcue.Core.Subtitles;

namespace Hushcue.Cli;

/// <summary>
/// Run the app's subtitle job on a test set and score the result: the word error rate against each passage's text,
/// and how many of its scored words (words that sound like other words, names) came out right, per preset.
/// The default set is eval/lookalike (passages.json + audio/NAME[.noisy].opus, made by tools/bench/make_lookalike.ps1).
/// </summary>
internal static class EvalCommand
{
    public const string Usage =
        """
        usage: Hushcue.Cli eval [--set DIR] [--preset NAME]... [--only TEXT] [--no-vocabulary] [--out DIR]
                                [--device cuda|cpu]
        """;

    private sealed record Passage(string Name, string Text, string? Vocabulary);

    public static async Task<int> RunAsync(string[] args)
    {
        string? setDir = null, outDir = null, device = null, only = null;
        var presets = new List<string>();
        var useVocabulary = true;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--set" when i + 1 < args.Length: setDir = args[++i]; break;
                case "--preset" when i + 1 < args.Length: presets.Add(args[++i]); break;
                case "--only" when i + 1 < args.Length: only = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--device" when i + 1 < args.Length: device = args[++i]; break;
                case "--no-vocabulary": useVocabulary = false; break;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }
        setDir ??= DefaultSet();
        if (presets.Count == 0)
            presets.Add(Presets.Default);
        if (setDir is null || !File.Exists(Path.Combine(setDir, "passages.json")) || presets.Any(p => Presets.All.All(q => q.Name != p)))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        outDir ??= Path.Combine(Path.GetTempPath(), "hushcue-eval");

        var passages = LoadPassages(Path.Combine(setDir, "passages.json"));
        var files = Directory.EnumerateFiles(Path.Combine(setDir, "audio"), "*.opus").Order()
                             .Where(f => only is null || Path.GetFileName(f).Contains(only, StringComparison.OrdinalIgnoreCase))
                             .Select(f => (File: f, Passage: passages[Path.GetFileName(f).Split('.')[0]]))
                             .ToList();
        var steps = new JobSteps { CudaDeviceCount = () => device == "cpu" ? 0 : WhisperModel.CudaDeviceCount() };

        foreach (var preset in presets)
        {
            var dir = Path.Combine(outDir, preset);
            Directory.CreateDirectory(dir);
            foreach (var (file, _) in files)
                File.Delete(Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".srt"));
            var clock = Stopwatch.StartNew();
            foreach (var group in files.GroupBy(f => useVocabulary ? f.Passage.Vocabulary ?? "" : ""))
            {
                var settings = new JobSettings { Preset = preset, Vocabulary = group.Key };
                await SubtitleJob.RunAsync([.. group.Select(f => f.File)], dir, settings, (_, _) => { }, _ => { }, null, null,
                                           steps, CancellationToken.None);
            }
            var seconds = clock.Elapsed.TotalSeconds;

            Console.WriteLine($"{preset} ({Presets.Get(preset).Model}{(useVocabulary ? "" : ", no vocabulary")}), {seconds:F0} s");
            Console.WriteLine($"  {"file",-22} {"words",5} {"WER",6}  {"scored words",12}  misses");
            int words = 0, errors = 0, scored = 0, right = 0;
            foreach (var (file, passage) in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var cues = await Srt.LoadAsync(Path.Combine(dir, name + ".srt"));
                var transcript = cues.SelectMany(c => WordScoring.Words(c.Text).Select(w => (w, c.Start))).ToList();
                var score = WordScoring.Compare(WordScoring.Reference(passage.Text), transcript);
                words += score.Words;
                errors += score.Errors;
                scored += score.Scored;
                right += score.ScoredRight;
                var misses = string.Join(", ", score.Misses.Select(m => $"{m.Expected} → {m.Got ?? "(none)"}{At(m.At)}"));
                Console.WriteLine($"  {name,-22} {score.Words,5} {score.ErrorRate,6:P1}  {$"{score.ScoredRight}/{score.Scored}",12}  {misses}");
            }
            Console.WriteLine($"  {"total",-22} {words,5} {(words == 0 ? 0 : (double)errors / words),6:P1}  {$"{right}/{scored}",12}");
            Console.WriteLine();
        }
        Console.WriteLine($"Subtitles: {outDir}");
        steps.Cache.Unload();
        return 0;
    }

    private static string At(double? seconds) => seconds is { } s ? $" ({(int)s / 60}:{(int)s % 60:00})" : "";

    private static Dictionary<string, Passage> LoadPassages(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("passages").EnumerateArray()
            .Select(p => new Passage(p.GetProperty("name").GetString()!, p.GetProperty("text").GetString()!,
                                     p.TryGetProperty("vocabulary", out var v) ? v.GetString() : null))
            .ToDictionary(p => p.Name);
    }

    /// <summary>eval/lookalike in the repository this program was built from.</summary>
    private static string? DefaultSet()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Hushcue.slnx")))
                return Path.Combine(dir.FullName, "eval", "lookalike");
        return null;
    }
}
