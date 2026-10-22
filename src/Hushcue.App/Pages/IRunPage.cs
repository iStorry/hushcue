using Hushcue.Services;

namespace Hushcue.Pages;

/// <summary>A batch's overall progress, for the taskbar button: 0..1, or null while nothing can be measured.</summary>
public sealed record RunProgress(double? Fraction, Taskbar.State State);

/// <summary>A page that runs a batch over its queue; the window shows its progress on the taskbar button.</summary>
public interface IRunPage
{
    bool IsRunning { get; }

    /// <summary>Ctrl+Enter: start, as the page's start button does.</summary>
    void Start();

    event EventHandler<RunProgress>? ProgressChanged;

    /// <summary>A batch ended (done, failed or cancelled).</summary>
    event EventHandler? Finished;
}
