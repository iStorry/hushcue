using Hushcue.Core.Engine;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Hushcue.Core.Review;

namespace Hushcue.Cli;

/// <summary>
/// The review screen's "Re-run this part" and "Try another model" from the console: transcribe one stretch of a file
/// with each preset given, print what each heard, and mark the words that differ from the first.
/// </summary>
internal static class RerunCommand
{
    public const string Usage =
        """
        usage: Hushcue.Cli rerun --from SECONDS --to SECONDS [--preset NAME]... [--device cuda|cpu] [--enhance] MEDIA
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        double? from = null, to = null;
        string? device = null, media = null;
        var presets = new List<string>();
        var settings = new JobSettings();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from" when i + 1 < args.Length: from = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--to" when i + 1 < args.Length: to = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--preset" when i + 1 < args.Length: presets.Add(args[++i]); break;
                case "--device" when i + 1 < args.Length: device = args[++i]; break;
                case "--enhance": settings = settings with { Enhance = true }; break;
                default: media = args[i]; break;
            }
        }
        if (presets.Count == 0)
            presets.AddRange(Presets.All.Select(p => p.Name));
        if (from is not { } start || to is not { } end || end <= start || media is null || presets.Any(n => Presets.All.All(p => p.Name != n)))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // the audio as the review reads it for a file made with the first preset
        var enhance = PartTranscription.Enhances(settings with { Preset = presets[0] });
        var audio = new PartAudio(media, await Audio.ReadAsync(media, enhance), enhance);
        var steps = new JobSteps { CudaDeviceCount = () => device == "cpu" ? 0 : WhisperModel.CudaDeviceCount() };
        string? first = null;
        foreach (var name in presets)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var cues = await PartTranscription.RunAsync(audio, start, end, settings with { Preset = name }, steps,
                                                        message => Console.Error.WriteLine("  " + message), CancellationToken.None);
            var text = string.Join(" ", cues.Select(c => c.Text));
            Console.WriteLine($"{name} ({Presets.Get(name).Model}), {clock.Elapsed.TotalSeconds:F1} s, {cues.Count} subtitle(s):");
            foreach (var cue in cues)
                Console.WriteLine($"  {CueTime.Range(cue.Start, cue.End)}  {cue.Text}");
            if (first is not null)
            {
                var (before, after) = TextDiff.Words(first, text);
                var changes = before.Count(w => w.Changed) + after.Count(w => w.Changed);
                Console.WriteLine(changes == 0
                    ? $"  the same words as {presets[0]}"
                    : $"  vs {presets[0]}: {string.Join(" ", after.Select(w => w.Changed ? $"[{w.Text}]" : w.Text))}"
                      + $"  (dropped: {string.Join(" ", before.Where(w => w.Changed).Select(w => w.Text))})");
            }
            first ??= text;
        }
        steps.Cache.Unload();
        return 0;
    }
}
