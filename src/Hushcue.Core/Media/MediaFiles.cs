namespace Hushcue.Core.Media;

public static class MediaFiles
{
    /// <summary>Video and audio types the app offers to open (anything ffmpeg reads works).</summary>
    public static readonly IReadOnlyList<string> Extensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg",
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus",
    ];
}
