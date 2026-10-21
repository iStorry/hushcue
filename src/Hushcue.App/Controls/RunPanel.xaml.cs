using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hushcue.Controls;

/// <summary>Status line, progress bar, collapsible log, and the Cancel / start buttons of a page.</summary>
public sealed partial class RunPanel : UserControl
{
    private const int MaxLogLines = 500;
    private readonly Queue<string> _log = new();

    public RunPanel() => InitializeComponent();

    public event EventHandler? StartRequested;
    public event EventHandler? CancelRequested;

    public string StartText
    {
        get => (string)StartButton.Content;
        set => StartButton.Content = value;
    }

    public void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
    }

    public void SetProgress(double fraction, string text)
    {
        Bar.Value = Math.Clamp(fraction, 0, 1) * 1000;
        SetStatus(text);
    }

    /// <param name="state">"done" (green), "error" (red) or "" (normal)</param>
    public void SetStatus(string text, string state = "")
    {
        if (text.Length > 0)
            StatusText.Text = text;
        StatusText.Foreground = (Brush)Application.Current.Resources[state switch
        {
            "done" => "SystemFillColorSuccessBrush",
            "error" => "SystemFillColorCriticalBrush",
            _ => "TextFillColorPrimaryBrush",
        }];
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

    private void OnCancel(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
