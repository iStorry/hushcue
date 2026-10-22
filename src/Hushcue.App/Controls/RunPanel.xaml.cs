using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Hushcue.Controls;

public enum StatusKind
{
    Normal,
    Done,
    Warning,
    Error,
}

/// <summary>The bottom bar of a page: the batch's status and progress, a collapsible log, Cancel and start.</summary>
public sealed partial class RunPanel : UserControl
{
    private const int MaxLogLines = 500;
    private readonly Queue<string> _log = new();
    private StatusKind _kind;

    public RunPanel()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => SetStatus("", _kind);
    }

    public event EventHandler? StartRequested;

    /// <summary>Cancel was asked for: true when by Esc, which asks first, false for the button.</summary>
    public event EventHandler<bool>? CancelRequested;

    public string StartText
    {
        get => (string)StartButton.Content;
        set => StartButton.Content = value;
    }

    public bool IsRunning => CancelButton.IsEnabled;

    public void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        if (running)
            Bar.Visibility = Visibility.Visible;
    }

    public void SetProgress(double fraction) => Bar.Value = Math.Clamp(fraction, 0, 1) * 1000;

    /// <summary>Hide the bar, e.g. when the queue is changed after a run.</summary>
    public void HideProgress()
    {
        if (!IsRunning)
            Bar.Visibility = Visibility.Collapsed;
    }

    /// <param name="text">the new status; empty keeps the current one (to change only its colour)</param>
    public void SetStatus(string text, StatusKind kind = StatusKind.Normal)
    {
        _kind = kind;
        var changed = text.Length > 0 && text != StatusText.Text;
        if (text.Length > 0)
            StatusText.Text = text;
        StatusText.Foreground = ThemeBrushes.Get(kind switch
        {
            StatusKind.Done => ThemeBrushes.Success,
            StatusKind.Warning => ThemeBrushes.Caution,
            StatusKind.Error => ThemeBrushes.Critical,
            _ => ThemeBrushes.Primary,
        });
        AutomationProperties.SetName(StatusText, StatusText.Text);
        // announce changes of state, not the time-left countdown
        if (changed && kind != StatusKind.Normal && FrameworkElementAutomationPeer.FromElement(StatusText) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public void Reset()
    {
        Bar.Value = 0;
        _log.Clear();
        LogBox.Text = "";
    }

    public void AppendLog(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines)
            _log.Dequeue();
        LogBox.Text = string.Join("\r", _log);
        LogBox.SelectionStart = LogBox.Text.Length;
    }

    public void SetCancelling()
    {
        SetStatus("Cancelling...");
        CancelButton.IsEnabled = false;
    }

    private void OnToggleLog(object sender, RoutedEventArgs e)
    {
        var show = LogBox.Visibility != Visibility.Visible;
        LogBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LogToggle.Content = show ? "Hide log" : "Show log";
    }

    private void OnStart(object sender, RoutedEventArgs e) => StartRequested?.Invoke(this, EventArgs.Empty);

    private void OnCancel(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, false);

    private void OnCancelShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (CancelButton.IsEnabled)
            CancelRequested?.Invoke(this, true);
    }
}
