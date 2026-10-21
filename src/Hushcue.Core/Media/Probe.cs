using System.Globalization;
using System.Text.Json;

namespace Hushcue.Core.Media;

public sealed record VideoInfo(int Width, int Height, double Duration, IReadOnlyList<string> AudioCodecs);

public static class Probe
{
    public static async Task<VideoInfo> VideoAsync(string path, CancellationToken cancel = default)
    {
        var (exitCode, output, errors) = await Tools.RunAsync(
            [Tools.Ffprobe, "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path], cancel);
        if (exitCode != 0)
        {
            var lines = errors.Trim().Split('\n');
            throw new MediaException("ffprobe could not read this file: " + lines[^1].Trim());
        }
        using var json = JsonDocument.Parse(output);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var video = streams.FirstOrDefault(s => CodecType(s) == "video");
        if (video.ValueKind == JsonValueKind.Undefined)
            throw new MediaException("This file has no video stream to put subtitles on.");
        var audio = streams.Where(s => CodecType(s) == "audio")
            .Select(s => s.TryGetProperty("codec_name", out var name) ? name.GetString() ?? "" : "")
            .ToList();
        var duration = json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
            ? double.Parse(d.GetString() ?? "0", CultureInfo.InvariantCulture)
            : 0;
        return new VideoInfo(video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), duration, audio);
    }

    private static string? CodecType(JsonElement stream) =>
        stream.TryGetProperty("codec_type", out var type) ? type.GetString() : null;
}
