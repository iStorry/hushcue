namespace Hushcue.Core.Pipeline;

internal enum JobStage
{
    Transcribing,
    Embedding,
}

/// <summary>
/// One progress value and status line for a job whose two stages run at the same time: the overall fraction is the
/// mean of every file's own fraction, and the status line shows what each stage is doing.
/// </summary>
internal sealed class JobProgress(int files, Action<double, string> report)
{
    private const string Separator = "  ·  ";
    private readonly double[] _done = new double[files];
    private readonly string[] _status = ["", ""];
    private readonly Lock _lock = new();

    /// <param name="fraction">how far file <paramref name="index"/> is through the whole job, 0..1</param>
    /// <param name="text">what the stage is doing; empty when it has nothing to do</param>
    public void Report(JobStage stage, int index, double fraction, string text)
    {
        lock (_lock)
        {
            _done[index] = fraction;
            _status[(int)stage] = text.Length > 0 ? $"[{index + 1}/{files}] {text}" : "";
            report(Math.Min(_done.Sum() / files, 1.0), string.Join(Separator, _status.Where(s => s.Length > 0)));
        }
    }
}
