using Hushcue.Core.Media;

namespace Hushcue.Core.Subtitles;

/// <summary>A video and the subtitles to open with it in the editor.</summary>
public sealed record SubtitlePair(string Video, string Srt);

/// <summary>
/// Finds which video an .srt belongs to and the other way round, by name in the same folder: "talk.mp4" goes with
/// "talk.srt" and with language-tagged names such as "talk.en.srt".
/// </summary>
public static class SubtitlePairs
{
    public static bool IsSubtitle(string path) => Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase);

    public static bool IsMedia(string path) =>
        MediaFiles.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The subtitles next to <paramref name="video"/>: "name.srt" first, else the first "name.*.srt".</summary>
    public static string? FindSubtitles(string video)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(video)) ?? "";
        var stem = Path.GetFileNameWithoutExtension(video);
        var exact = Path.Combine(folder, stem + ".srt");
        if (File.Exists(exact))
            return exact;
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, stem + ".*.srt").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
    }

    /// <summary>The video next to <paramref name="srt"/> that it was made for, or null.</summary>
    public static string? FindVideo(string srt)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(srt)) ?? "";
        foreach (var stem in Stems(srt))
        {
            foreach (var extension in MediaFiles.Extensions)
            {
                var candidate = Path.Combine(folder, stem + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Sort opened or dropped files: each .srt with its video (given alongside it, or found next to it) to open in the
    /// editor; .srt files without a video (Orphans); and everything else (Others), for the queue.
    /// </summary>
    public static (List<SubtitlePair> Pairs, List<string> Orphans, List<string> Others) Split(IEnumerable<string> paths)
    {
        var all = paths.ToList();
        var media = all.Where(IsMedia).ToList();
        var pairs = new List<SubtitlePair>();
        var orphans = new List<string>();
        foreach (var srt in all.Where(IsSubtitle))
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(srt)) ?? "";
            var stems = Stems(srt).ToList();
            var video = media.FirstOrDefault(m => stems.Contains(Path.GetFileNameWithoutExtension(m), StringComparer.OrdinalIgnoreCase)
                                                  && string.Equals(Path.GetDirectoryName(Path.GetFullPath(m)), folder, StringComparison.OrdinalIgnoreCase))
                        ?? FindVideo(srt);
            if (video is null)
                orphans.Add(srt);
            else
                pairs.Add(new SubtitlePair(video, srt));
        }
        var paired = pairs.Select(p => Path.GetFullPath(p.Video)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rest = all.Where(p => !IsSubtitle(p) && !paired.Contains(Path.GetFullPath(p))).ToList();
        return (pairs, orphans, rest);
    }

    /// <summary>"talk.en.srt" can belong to "talk.en.*" or to "talk.*".</summary>
    private static IEnumerable<string> Stems(string srt)
    {
        var stem = Path.GetFileNameWithoutExtension(srt);
        yield return stem;
        var dot = stem.LastIndexOf('.');
        if (dot > 0)
            yield return stem[..dot];
    }
}
