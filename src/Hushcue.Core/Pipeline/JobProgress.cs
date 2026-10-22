namespace Hushcue.Core.Pipeline;

internal enum JobStage
{
    Transcribing,
    Embedding,
}

/// <summary>
/// Progress of a job whose two stages run at the same time, reported two ways: one overall value and status line
/// (the mean of every file's own fraction, and what each stage is doing), and each file's own state.
/// </summary>
internal sealed class JobProgress(IReadOnlyList<string> files, Action<double, string> report, Action<FileProgress>? onFile)
{
    private const string Separator = "  ·  ";
    private readonly double[] _done = new double[files.Count];
    private readonly string[] _status = ["", ""];
    private readonly Lock _lock = new();

    /// <param name="fraction">how far file <paramref name="index"/> is through the whole job, 0..1</param>
    /// <param name="text">what the stage is doing to the file</param>
    /// <param name="step">what is being done, for the file's own state</param>
    /// <param name="rate">while transcribing: the measured speed and time left</param>
    public void Working(JobStage stage, int index, double fraction, string text, FileStage step, RateReading? rate = null) =>
        Update(stage, index, fraction, text,
               FileProgress.Working(files[index], fraction, text, step) with { Speed = rate?.Speed, TimeLeft = rate?.TimeLeft });

    /// <summary>The file is transcribed; the embed stage may still be busy with an earlier file.</summary>
    public void WaitingToEmbed(int index, double fraction) =>
        Update(JobStage.Transcribing, index, fraction, "",
               FileProgress.Working(files[index], fraction, "Waiting to add the subtitles...", FileStage.WaitingToEmbed));

    public void Done(JobStage stage, int index, string output) =>
        Update(stage, index, 1, "", FileProgress.Done(files[index], output));

    /// <summary>The file will not be worked on any more; it counts as finished in the overall value.</summary>
    public void Failed(JobStage stage, int index, string reason) =>
        Update(stage, index, 1, "", FileProgress.Failed(files[index], reason));

    private void Update(JobStage stage, int index, double fraction, string text, FileProgress file)
    {
        lock (_lock)
        {
            _done[index] = fraction;
            _status[(int)stage] = text.Length > 0 ? $"[{index + 1}/{files.Count}] {text}" : "";
            report(Math.Min(_done.Sum() / files.Count, 1.0), string.Join(Separator, _status.Where(s => s.Length > 0)));
            onFile?.Invoke(file);
        }
    }
}
