using System.Collections.Specialized;
using Hushcue.Core;
using Hushcue.Core.Review;
using Hushcue.Core.Subtitles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Controls;

/// <summary>A subtitle as a line of the live transcript (an app type: XAML can't describe Core's records).</summary>
public sealed class CueLine(Cue cue)
{
    public string Time { get; } = CueTime.Format(cue.Start);
    public string Text { get; } = cue.Text;
    public string AccessibleName => $"{Time}, {Text}";
}

/// <summary>
/// The subtitles of the file being transcribed, shown as they are heard (provisional until the file is finished), and
/// the finished ones afterwards.
/// </summary>
public sealed partial class LiveTranscript : UserControl
{
    private QueuedFile? _file;
    private Transcript? _shown;

    public LiveTranscript()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>Show <paramref name="file"/>'s subtitles, following it as more arrive.</summary>
    public void Show(QueuedFile? file)
    {
        if (_file is not null)
        {
            _file.LiveCues.CollectionChanged -= OnCuesAdded;
            _file.PropertyChanged -= OnFileChanged;
        }
        _file = file;
        if (file is not null)
        {
            file.LiveCues.CollectionChanged += OnCuesAdded;
            file.PropertyChanged += OnFileChanged;
        }
        Fill();
        ScrollToEnd();
    }

    /// <summary>The finished subtitles, or those heard so far.</summary>
    private void Fill()
    {
        _shown = _file?.Transcript;
        List.ItemsSource = _shown is { } transcript ? transcript.Cues.Select(c => new CueLine(c)).ToList() : _file?.LiveCues;
        Refresh();
    }

    private void OnCuesAdded(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Refresh();
        ScrollToEnd();
    }

    private void OnFileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(_file?.Transcript, _shown))
            Fill(); // the finished subtitles replace the provisional ones
        else
            Refresh();
    }

    private void Refresh()
    {
        var count = _shown?.Cues.Count ?? _file?.LiveCues.Count ?? 0;
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FileText.Text = _file is null ? ""
            : _shown is not null ? $"{_file.Name} · {count} subtitles"
            : _file.State == FileState.Working ? $"{_file.Name} · {count} so far, may still change"
            : _file.Name;
    }

    private void ScrollToEnd()
    {
        if (_shown is null && List.Items.Count > 0)
            List.ScrollIntoView(List.Items[List.Items.Count - 1]);
    }
}
