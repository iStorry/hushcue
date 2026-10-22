using System.Globalization;

namespace Hushcue.Core.Media;

/// <summary>
/// <para>How the subtitles end up in the result:</para>
/// <para>Soft: the SRT added as a subtitle track in an .mkv. Nothing is re-encoded (fast, lossless), but the player
/// must support subtitle tracks and the viewer can switch them off.</para>
/// <para>Burn: the text drawn onto the picture and re-encoded (.mp4). Works in every player and cannot be switched
/// off, at the cost of one generation of video quality.</para>
/// </summary>
public enum EmbedMode
{
    None,
    Soft,
    Burn,
}

/// <summary>Put subtitles inside the video file so there is no separate .srt to keep track of. A port of burn.py.</summary>
public static class Embed
{
    /// <summary>Audio codecs that can be stream-copied into an .mp4 untouched; anything else is re-encoded to AAC.</summary>
    public static readonly IReadOnlySet<string> Mp4AudioCopy = new HashSet<string> { "aac", "mp3", "ac3", "eac3" };

    /// <summary>libass sizes SRT text on a 288-line scale; 15 is about 5.5% of the picture height.</summary>
    public const double BaseFontSize = 15;

    public static readonly IReadOnlyList<string> NvencArgs =
        ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "21", "-b:v", "0"];

    public static readonly IReadOnlyList<string> X264Args = ["-c:v", "libx264", "-preset", "medium", "-crf", "19"];

    private static readonly Lazy<IReadOnlyList<string>> Encoder = new(DetectEncoder);

    /// <summary>libass force_style: bold white text with a black outline, sized to the shorter side of the picture.</summary>
    public static string BurnStyle(int width, int height)
    {
        var size = BaseFontSize * Math.Min(width, height) / height;
        return $"FontName=Arial,FontSize={size.ToString("G6", CultureInfo.InvariantCulture)},Bold=1,Outline=1.6,Shadow=0.8,MarginV=18";
    }

    public static List<string> AudioArgs(IEnumerable<string> codecs) =>
        codecs.All(Mp4AudioCopy.Contains) ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"];

    /// <summary>NVENC when it really works on this machine (checked once with a tiny test encode), else x264.</summary>
    public static IReadOnlyList<string> PickEncoder() => Encoder.Value;

    private static IReadOnlyList<string> DetectEncoder()
    {
        List<string> test = [Tools.Ffmpeg, "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2",
                             .. NvencArgs, "-f", "null", "-"];
        var (exitCode, _, _) = Tools.RunAsync(test).GetAwaiter().GetResult();
        return exitCode == 0 ? NvencArgs : X264Args;
    }

    /// <summary>The ffmpeg command; <paramref name="srtName"/> is a plain file name in ffmpeg's working directory.</summary>
    public static List<string> BuildCommand(EmbedMode mode, string video, string srtName, string output, VideoInfo info,
                                            IReadOnlyList<string>? encoder = null)
    {
        List<string> cmd = [Tools.Ffmpeg, "-y", "-v", "error", "-nostdin", "-progress", "pipe:1", "-nostats", "-i", video];
        return mode switch
        {
            EmbedMode.Burn =>
            [
                .. cmd, "-map", "0:v:0", "-map", "0:a?", "-sn", "-dn", "-map_chapters", "-1",
                "-vf", $"subtitles={srtName}:force_style='{BurnStyle(info.Width, info.Height)}'",
                .. encoder ?? PickEncoder(), "-pix_fmt", "yuv420p",
                .. AudioArgs(info.AudioCodecs), "-movflags", "+faststart", output,
            ],
            EmbedMode.Soft =>
            [
                .. cmd, "-i", srtName, "-map", "0:v", "-map", "0:a?", "-map", "1:0", "-map_chapters", "-1",
                "-c", "copy", "-c:s", "srt", "-metadata:s:s:0", "language=eng", "-disposition:s:0", "default", output,
            ],
            _ => throw new ArgumentException($"unknown embed mode {mode}", nameof(mode)),
        };
    }

    /// <summary>
    /// Where the result goes: "name.mkv" / "name.mp4" in the output folder (or next to the video). If that name is
    /// taken (burning an .mp4 in its own folder, or an earlier result), "name.subbed.ext" instead of overwriting.
    /// </summary>
    public static string OutputPath(string video, string? outDir, EmbedMode mode)
    {
        var suffix = mode == EmbedMode.Soft ? ".mkv" : ".mp4";
        var folder = outDir ?? Path.GetDirectoryName(video) ?? "";
        var stem = Path.GetFileNameWithoutExtension(video);
        var plain = Path.Combine(folder, stem + suffix);
        return File.Exists(plain) ? FileNames.Unique(Path.Combine(folder, $"{stem}.subbed{suffix}")) : plain;
    }

    /// <summary>Write a copy of <paramref name="video"/> with the subtitles from <paramref name="srt"/> inside it.</summary>
    public static async Task<string> SubtitlesAsync(string video, string srt, string? outDir, EmbedMode mode,
                                                    Action<double>? onFraction = null,
                                                    IReadOnlyList<string>? encoder = null,
                                                    CancellationToken cancel = default)
    {
        var info = await Probe.VideoAsync(video, cancel);
        var output = OutputPath(video, outDir, mode);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        // The subtitles filter parses its file name as part of a filter graph, where ':' '\' ''' '[' ',' are special.
        // Rather than escape them, give ffmpeg a plain name in a private working directory.
        var work = Directory.CreateTempSubdirectory("hushcue_burn_");
        try
        {
            File.Copy(srt, Path.Combine(work.FullName, "subs.srt"));
            var cmd = BuildCommand(mode, video, "subs.srt", output, info, encoder);
            await Tools.RunToFileAsync(cmd, work.FullName, info.Duration, output, onFraction ?? (_ => { }), cancel);
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (IOException)
            {
                // a killed ffmpeg can hold the folder for a moment; it is only a temp folder
            }
        }
        return output;
    }

    /// <summary>
    /// Remux a video (normally an .mkv) into an .mp4 without its subtitle tracks; nothing is re-encoded unless the
    /// audio must be. Burned-in subtitles are part of the picture and cannot be removed. With
    /// <paramref name="saveSrt"/> the first subtitle track is also written next to the new video as an .srt.
    /// </summary>
    public static async Task<string> StripSubtitlesAsync(string video, string? outDir, Action<double>? onFraction = null,
                                                         bool saveSrt = false, CancellationToken cancel = default)
    {
        var info = await Probe.VideoAsync(video, cancel);
        var folder = outDir ?? Path.GetDirectoryName(video) ?? "";
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(video);
        var output = FileNames.Unique(Path.Combine(folder, stem + ".mp4"));
        if (saveSrt)
        {
            var srt = FileNames.Unique(Path.Combine(folder, stem + ".srt"));
            var (exitCode, _, _) = await Tools.RunAsync(
                [Tools.Ffmpeg, "-y", "-v", "error", "-nostdin", "-i", video, "-map", "0:s:0", "-c:s", "srt", "-f", "srt", srt],
                cancel);
            if (exitCode != 0)
                File.Delete(srt); // no (text) subtitle track to save - not an error for the conversion
        }
        List<string> cmd =
        [
            Tools.Ffmpeg, "-y", "-v", "error", "-nostdin", "-progress", "pipe:1", "-nostats", "-i", video,
            "-map", "0:v:0", "-map", "0:a?", "-sn", "-dn", "-map_chapters", "-1",
            "-c:v", "copy", .. AudioArgs(info.AudioCodecs), "-movflags", "+faststart", output,
        ];
        await Tools.RunToFileAsync(cmd, null, info.Duration, output, onFraction ?? (_ => { }), cancel);
        return output;
    }

    /// <summary>
    /// StripSubtitlesAsync over a list of files. A file that fails is logged and skipped; the rest carry on, and the
    /// failures are reported together at the end. Originals go to the Recycle Bin only after their .mp4 is written.
    /// </summary>
    public static async Task<List<string>> ConvertFilesAsync(
        IReadOnlyList<string> files, string? outDir, bool saveSrt, bool deleteOriginal,
        Action<double, string> progress, Action<string> log, CancellationToken cancel,
        Action<FileProgress>? onFile = null, Func<string, bool>? recycle = null)
    {
        recycle ??= RecycleBin.Send;
        var outputs = new List<string>();
        var failed = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var src = files[i];
            var name = Path.GetFileName(src);
            log($"--- {name}");
            string output;
            try
            {
                var index = i;
                output = await StripSubtitlesAsync(src, outDir, f =>
                {
                    progress((index + f) / files.Count, $"[{index + 1}/{files.Count}] Converting {name}... {(int)(f * 100)}%");
                    onFile?.Invoke(FileProgress.Working(src, f, $"Converting · {(int)(f * 100)}%", FileStage.Converting));
                }, saveSrt, cancel);
            }
            catch (MediaException e)
            {
                log($"ERROR: could not convert {name}: {e.Message}");
                failed.Add(name);
                onFile?.Invoke(FileProgress.Failed(src, e.Message));
                continue;
            }
            log($"Saved {output}");
            outputs.Add(output);
            if (deleteOriginal)
            {
                log(new FileInfo(output).Length > 0 && recycle(src)
                    ? $"Moved the original {name} to the Recycle Bin"
                    : $"Kept the original {name} (could not move it to the Recycle Bin)");
            }
            onFile?.Invoke(FileProgress.Done(src, output));
        }
        if (failed.Count > 0)
            throw new MediaException("Could not convert: " + string.Join(", ", failed) + ". See the log for details.");
        progress(1.0, "Done");
        return outputs;
    }
}
