using Hushcue.Core.Media;
using Hushcue.Core.Review;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>A kept preview: the file made from it, the .srt left next to it (if any), and whether the original went to
/// the Recycle Bin.</summary>
/// <param name="Output">the .srt, or the new video</param>
public sealed record KeptPreview(string Output, string? Srt, bool Recycled);

/// <summary>
/// "Preview first" (<see cref="JobSettings.PreviewFirst"/>): a run's subtitles go into a folder of their own under
/// %TEMP%\Hushcue\previews instead of the output folder, and nothing else is made until the user keeps them (the .srt,
/// the subtitle track or the burned video, made then) or discards them (the folder is deleted).
/// </summary>
public static class Previews
{
    public static string Root => Path.Combine(Path.GetTempPath(), "Hushcue", "previews");

    /// <summary>Where a new preview of <paramref name="source"/> goes: "name.srt" in a new folder of its own.</summary>
    public static string NewSrtPath(string source, string? root = null)
    {
        var folder = Path.Combine(root ?? Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + ".srt");
    }

    /// <summary>Whether <paramref name="srt"/> is a preview: it is in a folder of its own under the previews folder.</summary>
    public static bool IsPreview(string? srt, string? root = null)
    {
        if (string.IsNullOrEmpty(srt))
            return false;
        var folder = Path.GetDirectoryName(Path.GetFullPath(srt));
        return folder is not null && string.Equals(Path.GetDirectoryName(folder), Path.GetFullPath(root ?? Root).TrimEnd('\\', '/'),
                                                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Delete a preview's folder. Anything that is not a preview is left alone.</summary>
    public static void Discard(string srt, string? root = null)
    {
        if (!IsPreview(srt, root))
            return;
        try
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetFullPath(srt))!, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a temporary folder; Windows or CleanUp gets it later
        }
    }

    /// <summary>Delete previews older than <paramref name="age"/>, left by an app that did not close normally.</summary>
    public static void CleanUp(TimeSpan age, string? root = null)
    {
        var folder = new DirectoryInfo(root ?? Root);
        if (!folder.Exists)
            return;
        foreach (var preview in folder.EnumerateDirectories().Where(d => d.LastWriteTimeUtc < DateTime.UtcNow - age))
        {
            try
            {
                preview.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // in use, or already gone
            }
        }
    }

    /// <summary>
    /// Keep a preview: make the result it was for, from <paramref name="cues"/> (the preview as edited), then delete
    /// the preview. An .srt goes next to the video (or into <paramref name="outDir"/>), never over an existing file; a
    /// subtitle track or burned video is made as a run makes it, with the .srt next to it when
    /// <paramref name="keepSrt"/>, and the original moved to the Recycle Bin when <paramref name="deleteOriginal"/>.
    /// If making the result fails, the preview is still there.
    /// </summary>
    /// <param name="preview">the preview's .srt</param>
    /// <param name="onFraction">how far ffmpeg is, 0..1, when embedding</param>
    public static Task<KeptPreview> KeepAsync(string source, string preview, IReadOnlyList<Cue> cues, IReadOnlyList<UnsureWord> unsure,
                                              string? outDir, EmbedMode mode, bool keepSrt, bool deleteOriginal,
                                              Action<double> onFraction, Action<string> log, CancellationToken cancel = default) =>
        KeepAsync(source, preview, cues, unsure, outDir, mode, keepSrt, deleteOriginal, onFraction, log, JobSteps.Default, null, cancel);

    internal static async Task<KeptPreview> KeepAsync(string source, string preview, IReadOnlyList<Cue> cues,
                                                      IReadOnlyList<UnsureWord> unsure, string? outDir, EmbedMode mode,
                                                      bool keepSrt, bool deleteOriginal, Action<double> onFraction,
                                                      Action<string> log, JobSteps steps, string? root, CancellationToken cancel)
    {
        if (cues.Count == 0)
            throw new MediaException("There are no subtitles to keep.");
        KeptPreview kept;
        if (mode == EmbedMode.None)
        {
            var srt = await SaveSrtAsync(source, cues, unsure, outDir, cancel);
            log($"Saved {srt} ({cues.Count} subtitles)");
            kept = new KeptPreview(srt, srt, Recycled: false);
        }
        else
        {
            var embedded = await SubtitleJob.EmbedAsync(source, cues, outDir, mode, deleteOriginal, onFraction, log, steps, cancel);
            var srt = keepSrt ? await SaveSrtAsync(source, cues, unsure, outDir, cancel) : null;
            if (srt is not null)
                log($"Saved {srt}");
            kept = new KeptPreview(embedded.Video, srt, embedded.Recycled);
        }
        Discard(preview, root);
        return kept;
    }

    private static async Task<string> SaveSrtAsync(string source, IReadOnlyList<Cue> cues, IReadOnlyList<UnsureWord> unsure,
                                                   string? outDir, CancellationToken cancel)
    {
        var folder = outDir ?? Path.GetDirectoryName(source) ?? "";
        Directory.CreateDirectory(folder);
        var srt = FileNames.Unique(Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + ".srt"));
        await Srt.SaveAsync(srt, cues, cancel);
        if (unsure.Count > 0)
            UnsureWords.Save(srt, unsure); // so a review opened later flags them too
        return srt;
    }
}
