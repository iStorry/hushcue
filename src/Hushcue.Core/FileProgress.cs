namespace Hushcue.Core;

public enum FileState
{
    Waiting,
    Working,
    Done,
    Failed,
    /// <summary>Transcribed, with the .srt written; embedding waits until someone has reviewed the subtitles.</summary>
    Review,
}

/// <summary>What is being done to a file while it is <see cref="FileState.Working"/>; None in the other states.</summary>
public enum FileStage
{
    None,
    /// <summary>Decoding the file's audio into memory.</summary>
    ReadingAudio,
    /// <summary>Loading the speech model (before the first file of a run, or after a switch to the CPU).</summary>
    LoadingModel,
    Transcribing,
    WritingSrt,
    /// <summary>Transcribed; the embed stage is still busy with an earlier file.</summary>
    WaitingToEmbed,
    /// <summary>Putting the subtitles into the video (a subtitle track, or burned in).</summary>
    Embedding,
    /// <summary>The MKV to MP4 tool remuxing the file.</summary>
    Converting,
}

/// <summary>Where one file of a batch job is, so a queue can show each file on its own.</summary>
/// <param name="Fraction">how far the file is through all of its work, 0..1</param>
/// <param name="Status">what is happening to the file, or why it failed: text to show as it is, never to parse</param>
/// <param name="Output">the file written, once the file is done</param>
/// <param name="Stage">what is being done while the file is working</param>
/// <param name="Speed">while transcribing: how many times faster than real time it goes, once measured</param>
/// <param name="TimeLeft">while transcribing: how long the rest of the file should take, once measured</param>
public sealed record FileProgress(string Path, FileState State, double Fraction, string Status, string? Output = null,
                                  FileStage Stage = FileStage.None, double? Speed = null, TimeSpan? TimeLeft = null)
{
    public static FileProgress Waiting(string path) => new(path, FileState.Waiting, 0, "");

    public static FileProgress Working(string path, double fraction, string status, FileStage stage) =>
        new(path, FileState.Working, fraction, status, Stage: stage);

    public static FileProgress Done(string path, string output) =>
        new(path, FileState.Done, 1, $"Saved {System.IO.Path.GetFileName(output)}", output);

    public static FileProgress Failed(string path, string reason) => new(path, FileState.Failed, 1, reason);

    /// <param name="srt">the subtitles waiting for review</param>
    public static FileProgress ReadyForReview(string path, string srt) => new(path, FileState.Review, 1, "Ready to review", srt);

    /// <summary>Reading the audio or loading the model: the file has started, but not the work that can be timed.</summary>
    public bool IsStarting => State == FileState.Working && Stage is FileStage.ReadingAudio or FileStage.LoadingModel;
}
