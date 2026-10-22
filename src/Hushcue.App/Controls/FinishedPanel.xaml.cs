using System.Diagnostics;
using Hushcue.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Controls;

/// <summary>What a finished batch did.</summary>
/// <param name="Video">length of the files that were done</param>
/// <param name="Notes">coverage warnings and failures, one line each, with the file's name</param>
/// <param name="Recycled">originals moved to the Recycle Bin, by their old paths</param>
/// <param name="Outputs">the files written</param>
/// <param name="Reviewable">files whose subtitles can be opened for review</param>
/// <param name="ToReview">files waiting for a review before their subtitles go into the video</param>
public sealed record RunSummary(int Done, int Failed, int Skipped, TimeSpan Video, TimeSpan Took, IReadOnlyList<string> Notes,
                                IReadOnlyList<string> Recycled, IReadOnlyList<string> Outputs,
                                IReadOnlyList<QueuedFile> Reviewable, int ToReview);

/// <summary>
/// The finished screen: how much video was done, how long it took and how fast, anything to check, and a way back for
/// originals moved to the Recycle Bin.
/// </summary>
public sealed partial class FinishedPanel : UserControl
{
    private RunSummary? _summary;

    public FinishedPanel() => InitializeComponent();

    /// <summary>"Back to options".</summary>
    public event EventHandler? Closed;

    /// <summary>One of the files' "Review" links.</summary>
    public event EventHandler<QueuedFile>? ReviewRequested;

    public void Show(RunSummary summary)
    {
        _summary = summary;
        var files = summary.Done == 1 ? "1 file done" : $"{summary.Done} files done";
        string[] parts =
        [
            summary.Done > 0 || summary.ToReview == 0 ? files : "",
            summary.ToReview > 0 ? $"{summary.ToReview} ready to review" : "",
            summary.Failed > 0 ? $"{summary.Failed} failed" : "", summary.Skipped > 0 ? $"{summary.Skipped} skipped" : "",
        ];
        HeadText.Text = string.Join(", ", parts.Where(p => p.Length > 0));
        HeadIcon.Glyph = summary.Failed > 0 ? "\uE7BA" : "\uE930";
        HeadIcon.Foreground = ThemeBrushes.Get(summary.Failed > 0 ? ThemeBrushes.Caution : ThemeBrushes.Success);

        VideoText.Text = Length(summary.Video);
        TookText.Text = Length(summary.Took);
        var speed = summary.Took.TotalSeconds > 0.5 ? summary.Video.TotalSeconds / summary.Took.TotalSeconds : 0;
        SpeedText.Text = speed > 0 ? $"{(speed >= 10 ? Math.Round(speed) : Math.Round(speed, 1))}×" : "–";
        Tiles.Visibility = summary.Done + summary.ToReview > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanel.Visibility = summary.Reviewable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewHint.Text = summary.ToReview > 0
            ? "Check and edit the subtitles, then embed each file from its review."
            : "Check and edit the subtitles; a review can also make a new video with them.";
        ReviewLinks.ItemsSource = summary.Reviewable;
        Notes.ItemsSource = summary.Notes;

        RecyclePanel.Visibility = summary.Recycled.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecycleText.Text = summary.Recycled.Count == 1
            ? "The original was moved to the Recycle Bin."
            : $"{summary.Recycled.Count} originals were moved to the Recycle Bin.";
        RestoreButton.Content = summary.Recycled.Count == 1 ? "Put it back" : "Put them back";
        RestoreButton.IsEnabled = true;
        OpenButton.IsEnabled = summary.Outputs.Count > 0;
        OpenButton.Focus(FocusState.Programmatic);
    }

    /// <summary>"1h 52m", "2m 18s", "48s": a tile's value.</summary>
    private static string Length(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
        : $"{Math.Max(Math.Round(span.TotalSeconds), 1):F0}s";

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_summary is null)
            return;
        var restored = _summary.Recycled.Count(RecycleBinRestore.Restore);
        var left = _summary.Recycled.Count - restored;
        RecycleText.Text = left == 0
            ? restored == 1 ? "The original is back where it was." : $"All {restored} originals are back where they were."
            : $"Put back {restored} of {_summary.Recycled.Count}. The rest are no longer in the Recycle Bin, or a file with the same name is in the way.";
        RestoreButton.IsEnabled = false;
    }

    private void OnOpenRecycleBin(object sender, RoutedEventArgs e) =>
        Process.Start("explorer.exe", "shell:RecycleBinFolder")?.Dispose();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_summary?.Outputs.FirstOrDefault(File.Exists) is { } output)
            Process.Start("explorer.exe", $"/select,\"{output}\"")?.Dispose();
    }

    private void OnBack(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);

    private void OnReview(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueuedFile file })
            ReviewRequested?.Invoke(this, file);
    }
}
