using System.Globalization;
using System.Text.Json;

namespace Hushcue.Core.Media;

/// <summary>What a queue needs to know about a file before a run: how long it is and which streams it has.</summary>
/// <param name="Duration">seconds; 0 when the container does not say</param>
public sealed record MediaInfo(double Duration, bool HasVideo, bool HasAudio);

/// <summary>A quick ffprobe of any file, unlike <see cref="Probe"/>, which needs a video stream to embed into.</summary>
public static class MediaProbe
{
    /// <exception cref="MediaException">ffprobe is missing or cannot read the file</exception>
    public static async Task<MediaInfo> ReadAsync(string path, CancellationToken cancel = default)
    {
        var (exitCode, output, errors) = await Tools.RunAsync(
            [Tools.Ffprobe, "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path], cancel);
        if (exitCode != 0)
        {
            var lines = errors.Trim().Split('\n');
            throw new MediaException("Not a video or audio file ffmpeg can read" + (lines[^1].Trim() is { Length: > 0 } last ? $" ({last})" : ""));
        }
        return Parse(output);
    }

    /// <summary>Reads ffprobe's JSON. Cover art (an attached picture) does not count as video.</summary>
    internal static MediaInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var hasVideo = false;
        var hasAudio = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var attached = stream.TryGetProperty("disposition", out var disposition)
                               && disposition.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1;
                hasVideo |= type == "video" && !attached;
                hasAudio |= type == "audio";
            }
        }
        var duration = root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
                       && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 0;
        return new MediaInfo(duration, hasVideo, hasAudio);
    }
}
