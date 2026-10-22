using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Pipeline;

/// <summary>
/// Something a <see cref="SubtitleJob"/> did that a front end may show, as data. The log says the same things in words
/// for people; nothing should parse the log.
/// </summary>
public abstract record JobEvent;

/// <summary>Where transcription runs: at the start of a run, and again if the GPU fails and the job moves to the CPU.</summary>
/// <param name="Device">"cuda" or "cpu"</param>
/// <param name="Fallback">the GPU failed and the job switched to the CPU</param>
/// <param name="Reason">why the CPU is used: no usable GPU, or the GPU's error</param>
public sealed record DeviceSelected(string Device, bool Fallback = false, string? Reason = null) : JobEvent;

/// <summary>The speech model is being downloaded before the run can start (only needed once).</summary>
/// <param name="Finished">the download is complete; the run carries on</param>
public sealed record ModelDownloading(string Model, long Done, long Total, bool Finished = false) : JobEvent;

/// <summary>
/// Subtitles heard so far in the file being transcribed, one decoded passage at a time, to show while the work goes
/// on. Provisional: loop recovery and the clean-up of the whole file can still change them; <see cref="FileTranscribed"/>
/// has the final ones.
/// </summary>
public sealed record CuesDecoded(string Path, IReadOnlyList<Cue> Cues) : JobEvent;

/// <summary>A file's audio is transcribed and its subtitles are final.</summary>
/// <param name="Cues">the finished subtitles</param>
/// <param name="AudioSeconds">length of the audio transcribed</param>
/// <param name="Took">time spent transcribing, from the start of decoding (after the model was loaded) until the
/// subtitles were ready</param>
/// <param name="Device">where it ran: "cuda" or "cpu"</param>
/// <param name="Srt">the .srt written, or null when it was only a temporary file for embedding</param>
/// <param name="Warnings">coverage warnings about the result (<see cref="Cleanup.CoverageWarnings"/>)</param>
public sealed record FileTranscribed(string Path, IReadOnlyList<Cue> Cues, double AudioSeconds, TimeSpan Took, string Device,
                                     string? Srt, IReadOnlyList<string> Warnings) : JobEvent
{
    /// <summary>Times faster than real time.</summary>
    public double Speed => Took.TotalSeconds > 0 ? AudioSeconds / Took.TotalSeconds : 0;
}

/// <summary>After its new video was written, the original was moved to the Recycle Bin (or could not be).</summary>
/// <param name="Path">the original's path, which <see cref="Media.RecycleBinRestore.Restore"/> puts it back to</param>
/// <param name="Moved">false: it could not be moved and is still in place</param>
public sealed record OriginalRecycled(string Path, bool Moved) : JobEvent;
