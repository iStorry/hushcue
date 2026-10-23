using Hushcue.Core.Review;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Hushcue.Controls;

/// <summary>A cue's new times after one of its edges was dragged.</summary>
public sealed record CueTimesEventArgs(int Index, double Start, double End);

/// <summary>
/// The timeline under the video: a time ruler, the audio's waveform and a block per subtitle, over a window of the file
/// that follows the playhead. Click to seek, click a block to select it, drag a block's edge to change its timing
/// (kept between its neighbours by <see cref="CueEditor.ClampTimes"/>), wheel to scroll, Ctrl+wheel to zoom. Drawn
/// with plain shapes: the waveform is one polygon of the window's min/max outline (<see cref="Waveform.Envelope"/>).
/// </summary>
public sealed partial class WaveformTimeline : Grid
{
    public const double MinViewSeconds = 4;
    private const double DefaultViewSeconds = 30;
    private const double RulerHeight = 18;
    private const double WaveTop = 22;
    private const double WaveHeight = 54;
    private const double LaneTop = 82;
    private const double LaneHeight = 30;
    private const double EdgeGrab = 6;

    private readonly Canvas _canvas = new();
    private readonly Canvas _ruler = new();
    private readonly Polygon _wave = new();
    private readonly Canvas _blocks = new();
    private readonly Rectangle _playhead = new() { Width = 2 };
    private readonly TextBlock _message = new() { Margin = new Thickness(0, WaveTop + 16, 0, 0) };
    private CueEditor? _editor;
    private Waveform? _waveform;
    private double _duration;
    private double _position;
    private int _selected = -1;
    private Drag? _drag;
    private bool _sizing;

    /// <summary>An edge being dragged: the cue, which edge, and the times it would get.</summary>
    private sealed record Drag(int Index, bool StartEdge, double Start, double End, double OldStart, double OldEnd);

    public WaveformTimeline()
    {
        Height = LaneTop + LaneHeight + 4;
        _canvas.Background = new SolidColorBrush(Colors.Transparent); // hit-testable everywhere
        _canvas.Children.Add(_ruler);
        _canvas.Children.Add(_wave);
        _canvas.Children.Add(_blocks);
        _canvas.Children.Add(_playhead);
        _canvas.Children.Add(_message);
        Children.Add(_canvas);
        AutomationProperties.SetName(this, "Timeline: waveform and a block for each subtitle. Drag the edges of a block to change its timing; the start and end boxes do the same from the keyboard.");
        SizeChanged += (_, e) =>
        {
            // a Canvas draws its shapes past its edges too: blocks and labels at the ends of the view spilled over the card
            _canvas.Clip = new RectangleGeometry { Rect = new Rect(new Point(0, 0), e.NewSize) };
            Redraw();
        };
        ActualThemeChanged += (_, _) => Redraw();
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        _canvas.PointerCaptureLost += (_, _) => EndDrag(commit: false);
        _canvas.PointerWheelChanged += OnWheel;
    }

    /// <summary>Seconds at the left edge.</summary>
    public double ViewStart { get; private set; }

    /// <summary>Seconds shown across the width.</summary>
    public double ViewSeconds { get; private set; } = DefaultViewSeconds;

    /// <summary>A click away from the subtitle blocks: play from there.</summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>A subtitle block was clicked.</summary>
    public event EventHandler<int>? CueClicked;

    /// <summary>An edge was dragged and let go somewhere new.</summary>
    public event EventHandler<CueTimesEventArgs>? TimesChanged;

    public void SetSource(CueEditor editor, double duration)
    {
        _editor = editor;
        _duration = duration;
        _selected = -1;
        ViewStart = 0;
        ViewSeconds = Math.Clamp(DefaultViewSeconds, MinViewSeconds, Math.Max(MinViewSeconds, duration));
        Redraw();
    }

    /// <param name="message">shown instead while there is no waveform ("Reading the audio...")</param>
    public void SetWaveform(Waveform? waveform, string message = "")
    {
        _waveform = waveform;
        _message.Text = waveform is null ? message : "";
        if (waveform is not null)
            _duration = Math.Max(_duration, waveform.Duration);
        Redraw();
    }

    public void SetSelected(int index)
    {
        _selected = index;
        DrawBlocks();
    }

    /// <summary>The cues changed: draw them again.</summary>
    public void RefreshCues() => DrawBlocks();

    /// <param name="follow">scroll so the playhead stays in view</param>
    public void SetPosition(double seconds, bool follow)
    {
        _position = seconds;
        if (follow && _drag is null && (seconds < ViewStart || seconds > ViewStart + ViewSeconds * 0.95))
            ScrollTo(seconds - ViewSeconds * 0.1);
        else
            PlaceHead();
    }

    /// <summary>Scroll so [<paramref name="start"/>, <paramref name="end"/>] is in view, centred when it was not.</summary>
    public void Reveal(double start, double end)
    {
        if (start < ViewStart || end > ViewStart + ViewSeconds)
            ScrollTo((start + end) / 2 - ViewSeconds / 2);
    }

    /// <param name="factor">below 1 zooms in</param>
    /// <param name="around">the time that stays put, the playhead by default</param>
    public void Zoom(double factor, double? around = null)
    {
        var anchor = around ?? _position;
        var share = (anchor - ViewStart) / ViewSeconds;
        ViewSeconds = Math.Clamp(ViewSeconds * factor, MinViewSeconds, Math.Max(MinViewSeconds, _duration));
        ViewStart = anchor - share * ViewSeconds;
        ClampView();
        Redraw();
    }

    private void ScrollTo(double start)
    {
        ViewStart = start;
        ClampView();
        Redraw();
    }

    private void ClampView() => ViewStart = Math.Clamp(ViewStart, 0, Math.Max(0, _duration - ViewSeconds));

    private double X(double seconds) => (seconds - ViewStart) / ViewSeconds * ActualWidth;

    private double Seconds(double x) => ViewStart + x / Math.Max(ActualWidth, 1) * ViewSeconds;

    private void Redraw()
    {
        DrawRuler();
        DrawWave();
        DrawBlocks();
        PlaceHead();
    }

    private void DrawRuler()
    {
        _ruler.Children.Clear();
        if (ActualWidth < 2)
            return;
        var step = NiceStep(ViewSeconds / Math.Max(ActualWidth / 90, 1));
        var brush = ThemeBrushes.Get(ThemeBrushes.Secondary);
        for (var t = Math.Ceiling(ViewStart / step) * step; t < ViewStart + ViewSeconds; t += step)
        {
            var x = X(t);
            var tick = new Rectangle { Width = 1, Height = 5, Fill = brush };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, RulerHeight - 5);
            var label = new TextBlock { Text = CueTime.Format(t, 0), FontSize = 11, Foreground = brush };
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 0);
            _ruler.Children.Add(tick);
            _ruler.Children.Add(label);
        }
    }

    /// <summary>1, 2, 5, 10, 15, 30 s, 1, 2, 5, 10 min... at least <paramref name="seconds"/>.</summary>
    private static double NiceStep(double seconds) =>
        new double[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 }.FirstOrDefault(s => s >= seconds, 7200);

    private void DrawWave()
    {
        var columns = (int)ActualWidth;
        _message.Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary);
        if (_waveform is null || columns < 2)
        {
            _wave.Points = [];
            return;
        }
        var (min, max) = _waveform.Envelope(ViewStart, ViewStart + ViewSeconds, columns);
        const double middle = WaveTop + WaveHeight / 2, half = WaveHeight / 2;
        var points = new PointCollection();
        for (var i = 0; i < columns; i++)
            points.Add(new Point(i, middle - max[i] * half - 0.5));
        for (var i = columns - 1; i >= 0; i--)
            points.Add(new Point(i, middle - min[i] * half + 0.5));
        _wave.Points = points;
        _wave.Fill = ThemeBrushes.Get("TextFillColorTertiaryBrush");
    }

    private void DrawBlocks()
    {
        _blocks.Children.Clear();
        if (_editor is null || ActualWidth < 2)
            return;
        var flagged = _editor.Flags.Where(f => f.Cue is not null).Select(f => f.Cue!.Value).ToHashSet();
        var accent = Application.Current.Resources["SystemAccentColor"] is Color c ? c : Colors.SteelBlue;
        var cues = _editor.Cues;
        for (var i = 0; i < cues.Count; i++)
        {
            var (start, end) = _drag is { } drag && drag.Index == i ? (drag.Start, drag.End) : (cues[i].Start, cues[i].End);
            if (end < ViewStart || start > ViewStart + ViewSeconds)
                continue;
            var selected = i == _selected;
            var block = new Border
            {
                Width = Math.Max(2, X(end) - X(start)),
                Height = LaneHeight,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Background = selected ? ThemeBrushes.Get(ThemeBrushes.Accent)
                    : flagged.Contains(i) ? ThemeBrushes.Get("SystemFillColorCautionBackgroundBrush")
                    : new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B)),
                BorderBrush = flagged.Contains(i) && !selected ? ThemeBrushes.Get(ThemeBrushes.Caution)
                    : new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)),
                Child = new TextBlock
                {
                    Text = cues[i].Text,
                    FontSize = 12,
                    Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = ThemeBrushes.Get(selected ? "TextOnAccentFillColorPrimaryBrush" : ThemeBrushes.Primary),
                },
            };
            Canvas.SetLeft(block, X(start));
            Canvas.SetTop(block, LaneTop);
            _blocks.Children.Add(block);
        }
    }

    private void PlaceHead()
    {
        var x = X(_position);
        _playhead.Height = Height;
        _playhead.Fill = ThemeBrushes.Get(ThemeBrushes.Critical);
        _playhead.Visibility = x >= 0 && x <= ActualWidth ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_playhead, x - 1);
    }

    /// <summary>The cue edge under <paramref name="point"/>, if any: the closest one within <see cref="EdgeGrab"/> pixels.</summary>
    private (int Index, bool StartEdge)? EdgeAt(Point point)
    {
        if (_editor is null || point.Y < LaneTop - 4 || point.Y > LaneTop + LaneHeight + 4)
            return null;
        (int, bool)? best = null;
        var bestDistance = EdgeGrab;
        var cues = _editor.Cues;
        for (var i = 0; i < cues.Count; i++)
        {
            if (cues[i].End < ViewStart || cues[i].Start > ViewStart + ViewSeconds)
                continue;
            // between two touching cues, the side the pointer is on wins
            var toStart = Math.Abs(point.X - X(cues[i].Start)) - (point.X >= X(cues[i].Start) ? 0.1 : 0);
            var toEnd = Math.Abs(point.X - X(cues[i].End)) - (point.X <= X(cues[i].End) ? 0.1 : 0);
            if (toStart <= bestDistance)
                (best, bestDistance) = ((i, true), toStart);
            if (toEnd <= bestDistance)
                (best, bestDistance) = ((i, false), toEnd);
        }
        return best;
    }

    private int CueAt(Point point)
    {
        if (_editor is null || point.Y < LaneTop || point.Y > LaneTop + LaneHeight)
            return -1;
        return _editor.IndexAt(Seconds(point.X));
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed || _editor is null)
            return;
        if (EdgeAt(point.Position) is { } edge)
        {
            var cue = _editor.Cues[edge.Index];
            _drag = new Drag(edge.Index, edge.StartEdge, cue.Start, cue.End, cue.Start, cue.End);
            _canvas.CapturePointer(e.Pointer);
            CueClicked?.Invoke(this, edge.Index);
        }
        else if (CueAt(point.Position) is var index and >= 0)
        {
            CueClicked?.Invoke(this, index);
        }
        else
        {
            SeekRequested?.Invoke(this, Math.Clamp(Seconds(point.Position.X), 0, _duration));
        }
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas).Position;
        if (_drag is { } drag && _editor is not null)
        {
            var t = Seconds(point.X);
            var (start, end) = drag.StartEdge ? _editor.ClampTimes(drag.Index, t, drag.OldEnd) : _editor.ClampTimes(drag.Index, drag.OldStart, t);
            _drag = drag with { Start = start, End = end };
            DrawBlocks();
            e.Handled = true;
            return;
        }
        var sizing = EdgeAt(point) is not null;
        if (sizing != _sizing)
        {
            _sizing = sizing;
            ProtectedCursor = sizing ? InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast) : null;
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null)
            return;
        _canvas.ReleasePointerCapture(e.Pointer);
        EndDrag(commit: true);
        e.Handled = true;
    }

    private void EndDrag(bool commit)
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        if (commit && (drag.Start != drag.OldStart || drag.End != drag.OldEnd))
            TimesChanged?.Invoke(this, new CueTimesEventArgs(drag.Index, drag.Start, drag.End));
        DrawBlocks();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
            return;
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
            Zoom(delta > 0 ? 0.8 : 1.25, Seconds(point.Position.X));
        else
            ScrollTo(ViewStart - Math.Sign(delta) * ViewSeconds * 0.15);
        e.Handled = true;
    }
}
