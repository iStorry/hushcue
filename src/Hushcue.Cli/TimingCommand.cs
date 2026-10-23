using Hushcue.Core.Engine;
using Hushcue.Core.Evaluation;
using Hushcue.Core.Pipeline;
using Hushcue.Core.Subtitles;

namespace Hushcue.Cli;

/// <summary>
/// Run the app's job on one file and score its word timing and words against subtitles with exact phrase times
/// (eval/speaking-styles/speaking-styles.srt, made by tools/bench/make_speaking_styles.py): how far the first and
/// last word of each phrase are from where the phrase starts and ends, and the word error rate.
/// </summary>
internal static class TimingCommand
{
    public const string Usage =
        """
        usage: Hushcue.Cli timing --reference SRT [--preset NAME] [--device cuda|cpu] [--out DIR] [--no-word-edges]
                                  [--chunk-on-silence] [--no-punctuation-prompt | --punctuation-prompt TEXT] MEDIA
        """;

    private const int WorstShown = 5;

    public static async Task<int> RunAsync(string[] args)
    {
        string? reference = null, device = null, media = null;
        var outDir = Path.Combine(Path.GetTempPath(), "hushcue-timing");
        var settings = new JobSettings();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--reference" when i + 1 < args.Length: reference = args[++i]; break;
                case "--preset" when i + 1 < args.Length: settings = settings with { Preset = args[++i] }; break;
                case "--device" when i + 1 < args.Length: device = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--no-word-edges": settings = settings with { TrimWordEdges = false }; break;
                case "--chunk-on-silence": settings = settings with { ChunkOnSilence = true }; break;
                case "--no-punctuation-prompt": settings = settings with { PunctuationPrompt = null }; break;
                case "--punctuation-prompt" when i + 1 < args.Length: settings = settings with { PunctuationPrompt = args[++i] }; break;
                default: media = args[i]; break;
            }
        }
        if (reference is null || media is null || Presets.All.All(p => p.Name != settings.Preset))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        Directory.CreateDirectory(outDir);
        File.Delete(Path.Combine(outDir, Path.GetFileNameWithoutExtension(media) + ".srt"));
        var steps = new JobSteps { CudaDeviceCount = () => device == "cpu" ? 0 : WhisperModel.CudaDeviceCount() };
        IReadOnlyList<Word> words = [];
        IReadOnlyList<Cue> cues = [];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await SubtitleJob.RunAsync([media], outDir, settings, (_, _) => { }, _ => { }, null,
                                   e => { if (e is FileTranscribed t) (words, cues) = (t.Words, t.Cues); },
                                   steps, CancellationToken.None);
        steps.Cache.Unload();

        var phrases = await Srt.LoadAsync(reference);
        var timing = TimingScoring.Compare(phrases, words);
        var transcript = cues.SelectMany(c => WordScoring.Words(c.Text).Select(w => (w, c.Start))).ToList();
        var score = WordScoring.Compare(WordScoring.Reference(string.Join(' ', phrases.Select(p => p.Text))), transcript);

        Console.WriteLine($"{settings.Preset}, {clock.Elapsed.TotalSeconds:F0} s: {timing.Heard} of {phrases.Count} phrases heard, "
                          + $"WER {score.ErrorRate:P1} ({score.Errors} of {score.Words} words), {cues.Count} subtitles");
        Describe("start", [.. timing.StartErrors]);
        Describe("end", [.. timing.EndErrors]);
        Console.WriteLine("  furthest off:");
        foreach (var phrase in timing.Phrases.Where(p => p.StartError is not null)
                                     .OrderByDescending(p => Math.Max(Math.Abs(p.StartError ?? 0), Math.Abs(p.EndError ?? 0)))
                                     .Take(WorstShown))
        {
            Console.WriteLine($"    {phrase.StartError,6:+0.00;-0.00} {phrase.EndError,6:+0.00;-0.00}  "
                              + $"{Hushcue.Core.Review.CueTime.Range(phrase.Reference.Start, phrase.Reference.End)}  {phrase.Reference.Text}");
        }
        return 0;
    }

    /// <summary>Errors in seconds, positive when late.</summary>
    private static void Describe(string edge, IReadOnlyList<double> errors)
    {
        var absolute = errors.Select(Math.Abs).ToList();
        string Within(double seconds) => $"{(errors.Count == 0 ? 0 : absolute.Count(e => e <= seconds) / (double)errors.Count),4:P0}";
        Console.WriteLine($"  {edge,-5}  median |error| {TimingScoring.Median(absolute):F3} s, "
                          + $"mean {(errors.Count == 0 ? 0 : errors.Average()):+0.000;-0.000} s, "
                          + $"within 0.1 s {Within(0.1)}, within 0.25 s {Within(0.25)}");
    }
}
