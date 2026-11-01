using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Hushcue.Controls;

/// <summary>
/// The strip between two side-by-side panes: drag it, or use Left and Right while it has focus, to share the room
/// differently; double-click for the default again. The page does the sizing. What it looks like is its content.
/// </summary>
public sealed partial class PaneDivider : UserControl
{
    private const double KeyStep = 20;

    /// <summary>Where the drag started in the window: the divider itself moves along with the pointer.</summary>
    private double? _pressedAt;

    public PaneDivider()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    /// <summary>A move begins; the distances in <see cref="Dragged"/> count from here.</summary>
    public event EventHandler? DragStarted;

    /// <summary>How far right of where it started the divider should be now, in effective pixels.</summary>
    public event EventHandler<double>? Dragged;

    /// <summary>Double-clicked: back to the default.</summary>
    public event EventHandler? Reset;

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        _pressedAt = point.Position.X;
        CapturePointer(e.Pointer);
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressedAt is not { } start)
            return;
        Dragged?.Invoke(this, e.GetCurrentPoint(null).Position.X - start);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedAt is null)
            return;
        _pressedAt = null;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressedAt = null;
    }

    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        base.OnDoubleTapped(e);
        Reset?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right))
            return;
        DragStarted?.Invoke(this, EventArgs.Empty);
        Dragged?.Invoke(this, e.Key == VirtualKey.Left ? -KeyStep : KeyStep);
        e.Handled = true;
    }
}
