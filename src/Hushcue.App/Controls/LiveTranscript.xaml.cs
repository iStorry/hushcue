using System.Collections.Specialized;
using Hushcue.Core;
using Hushcue.Core.Review;
using Hushcue.Core.Subtitles;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
    /// <summary>Within this many pixels of the end counts as reading the newest lines.</summary>
    private const double EndSlack = 48;

    private QueuedFile? _file;
    private Transcript? _shown;
    private ScrollViewer? _scroller;

    /// <summary>Keep the newest lines in view; off while the user has scrolled up to read.</summary>
    private bool _follow = true;

    public LiveTranscript()
    {
        InitializeComponent();
        List.Loaded += (_, _) =>
        {
            if (_scroller is not null || FindScroller(List) is not { } scroller)
                return;
            _scroller = scroller;
            _scroller.ViewChanged += (_, e) =>
            {
                if (!e.IsIntermediate)
                    _follow = _scroller.VerticalOffset >= _scroller.ScrollableHeight - EndSlack;
            };
        };
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
        _follow = true;
        ScrollToEnd(animate: false);
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
        if (_follow)
            ScrollToEnd(animate: true);
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

    /// <summary>Scroll to the newest line once the list has laid it out (scrolling the viewer, not ScrollIntoView,
    /// which jumped and blanked the list for a moment on every new line).</summary>
    private void ScrollToEnd(bool animate)
    {
        if (_shown is not null || List.Items.Count == 0)
            return;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_scroller is null)
            {
                List.ScrollIntoView(List.Items[^1]);
                return;
            }
            List.UpdateLayout();
            _scroller.ChangeView(null, _scroller.ScrollableHeight, null, disableAnimation: !animate);
        });
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as ScrollViewer ?? FindScroller(child)) is { } found)
                return found;
        }
        return null;
    }
}
