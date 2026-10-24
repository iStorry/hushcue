using System.Globalization;
using System.Text.Json;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Media;

/// <summary>A subtitle stream inside a video file, as ffprobe lists it.</summary>
/// <param name="Index">the stream's index in the whole file (ffmpeg's <c>-map 0:Index</c>)</param>
/// <param name="Language">the language tag as the file has it ("eng", "en"), or null when it has none</param>
public sealed record SubtitleTrack(int Index, string Codec, string? Language, string? Title, bool IsDefault, bool IsForced)
{
    /// <summary>Text formats ffmpeg can turn into SubRip.</summary>
    private static readonly IReadOnlySet<string> TextCodecs = new HashSet<string>
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "subviewer", "subviewer1", "realtext",
        "sami", "mpl2", "pjs", "vplayer", "jacosub", "stl",
    };

    private static readonly Dictionary<string, string> Formats = new()
    {
        ["subrip"] = "SRT", ["srt"] = "SRT", ["ass"] = "ASS", ["ssa"] = "SSA", ["webvtt"] = "WebVTT",
        ["mov_text"] = "MP4 text", ["hdmv_pgs_subtitle"] = "Blu-ray PGS", ["dvd_subtitle"] = "DVD VobSub",
        ["dvb_subtitle"] = "DVB", ["xsub"] = "DivX XSUB", ["dvb_teletext"] = "teletext",
    };

    /// <summary>ISO 639-2 has a second ("bibliographic") code for these languages, which video files often use.</summary>
    private static readonly Dictionary<string, string> TerminologyCodes = new()
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho", ["cze"] = "ces",
        ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu", ["gre"] = "ell", ["ice"] = "isl",
        ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk",
        ["tib"] = "bod", ["wel"] = "cym",
    };

    /// <summary>Text the editor can open; otherwise pictures of text (Blu-ray, DVD), which would need OCR.</summary>
    public bool IsText => TextCodecs.Contains(Codec);

    /// <summary>"Blu-ray PGS", "ASS", ...: the format as a person would name it.</summary>
    public string Format => Formats.GetValueOrDefault(Codec, Codec);

    /// <summary>"English · SDH · forced · ASS": what tells this track from the others in a list.</summary>
    public string Label
    {
        get
        {
            List<string> parts = [LanguageName(Language) ?? "Unknown language"];
            if (!string.IsNullOrWhiteSpace(Title))
                parts.Add(Title.Trim());
            if (IsForced)
                parts.Add("forced");
            if (IsDefault)
                parts.Add("default");
            parts.Add(Format);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>"English" for "eng", "en" or "ger" → "German"; null for no tag or "und" (undetermined).</summary>
    public static string? LanguageName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("und", StringComparison.OrdinalIgnoreCase))
            return null;
        var code = tag.Trim().ToLowerInvariant();
        code = TerminologyCodes.GetValueOrDefault(code, code);
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(c => c.ThreeLetterISOLanguageName == code || c.TwoLetterISOLanguageName == code || c.Name == code);
        return culture is { EnglishName.Length: > 0 } && culture.Name.Length > 0 ? culture.EnglishName : tag.Trim();
    }
}

/// <summary>Reading the subtitle tracks inside a video (.mkv, .mp4) and saving one as an .srt to edit.</summary>
public static class SubtitleTracks
{
    /// <exception cref="MediaException">ffprobe is missing or cannot read the file</exception>
    public static async Task<List<SubtitleTrack>> ReadAsync(string video, CancellationToken cancel = default)
    {
        var (exitCode, output, errors) = await Tools.RunAsync(
            [Tools.Ffprobe, "-v", "error", "-print_format", "json", "-show_streams", "-select_streams", "s", video], cancel);
        if (exitCode != 0)
        {
            var lines = errors.Trim().Split('\n');
            throw new MediaException("ffprobe could not read this file: " + lines[^1].Trim());
        }
        return Parse(output);
    }

    /// <summary>The subtitle streams in ffprobe's JSON, in file order.</summary>
    internal static List<SubtitleTrack> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var tracks = new List<SubtitleTrack>();
        if (!document.RootElement.TryGetProperty("streams", out var streams))
            return tracks;
        foreach (var stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("codec_type", out var type) || type.GetString() != "subtitle"
                || !stream.TryGetProperty("index", out var index))
                continue;
            var codec = stream.TryGetProperty("codec_name", out var name) ? name.GetString() ?? "" : "";
            stream.TryGetProperty("tags", out var tags);
            stream.TryGetProperty("disposition", out var disposition);
            tracks.Add(new SubtitleTrack(index.GetInt32(), codec, Tag(tags, "language"), Tag(tags, "title"),
                                         Flag(disposition, "default"), Flag(disposition, "forced")));
        }
        return tracks;
    }

    /// <summary>
    /// Where a track's .srt goes: next to the video as "name.lang.srt" ("name.srt" without a language), so the editor
    /// finds it by name the next time the video is opened. Never an existing file.
    /// </summary>
    public static string SrtPath(string video, SubtitleTrack track)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(video)) ?? "";
        var stem = Path.GetFileNameWithoutExtension(video);
        var language = new string((track.Language ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        var tag = language.Length > 0 && !language.Equals("und", StringComparison.OrdinalIgnoreCase) ? "." + language : "";
        return FileNames.Unique(Path.Combine(folder, stem + tag + ".srt"));
    }

    /// <summary>Save a text track as SubRip at <paramref name="srt"/>. Nothing is left behind if it fails.</summary>
    /// <exception cref="MediaException">the track is pictures, or ffmpeg could not read it</exception>
    public static async Task ExtractAsync(string video, SubtitleTrack track, string srt, CancellationToken cancel = default)
    {
        if (!track.IsText)
            throw new MediaException($"This track is pictures ({track.Format}), not text.");
        int exitCode;
        string errors;
        try
        {
            (exitCode, _, errors) = await Tools.RunAsync(
                [Tools.Ffmpeg, "-y", "-v", "error", "-nostdin", "-i", video, "-map", $"0:{track.Index}", "-c:s", "srt", "-f", "srt", srt],
                cancel);
        }
        catch
        {
            Tools.DeleteWhenReleased(srt);
            throw;
        }
        // an .srt with nothing in it would be found next to the video every time it is opened, so it is not kept
        if (exitCode != 0 || !File.Exists(srt) || Srt.Read(await File.ReadAllTextAsync(srt, cancel)).Count == 0)
        {
            Tools.DeleteWhenReleased(srt);
            var lines = errors.Trim().Split('\n');
            throw new MediaException(exitCode != 0
                ? "ffmpeg could not read the subtitle track: " + lines[^1].Trim()
                : "The subtitle track has no subtitles in it.");
        }
    }

    private static string? Tag(JsonElement tags, string name) =>
        tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static bool Flag(JsonElement disposition, string name) =>
        disposition.ValueKind == JsonValueKind.Object && disposition.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.GetInt32() == 1;
}
