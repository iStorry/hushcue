namespace Hushcue.Core;

public enum FileState
{
    Waiting,
    Working,
    Done,
    Failed,
}

/// <summary>Where one file of a batch job is, so a queue can show each file on its own.</summary>
/// <param name="Fraction">how far the file is through all of its work, 0..1</param>
/// <param name="Status">what is happening to the file, or why it failed</param>
/// <param name="Output">the file written, once the file is done</param>
public sealed record FileProgress(string Path, FileState State, double Fraction, string Status, string? Output = null)
{
    public static FileProgress Waiting(string path) => new(path, FileState.Waiting, 0, "");

    public static FileProgress Working(string path, double fraction, string status) =>
        new(path, FileState.Working, fraction, status);

    public static FileProgress Done(string path, string output) =>
        new(path, FileState.Done, 1, $"Saved {System.IO.Path.GetFileName(output)}", output);

    public static FileProgress Failed(string path, string reason) => new(path, FileState.Failed, 1, reason);
}
