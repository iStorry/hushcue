using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Controls;

/// <summary>
/// Makes a ScrollViewer's content exactly as tall as the ScrollViewer, but never less than <c>MinHeight</c>: a page
/// laid out to fill the window (lists that scroll by themselves, cards that share the height) keeps that layout on a
/// tall window, and on a short one scrolls as a whole instead of squeezing its parts until they are cut off.
/// </summary>
public static class ScrollFill
{
    public static readonly DependencyProperty MinHeightProperty = DependencyProperty.RegisterAttached(
        "MinHeight", typeof(double), typeof(ScrollFill), new PropertyMetadata(0.0, OnMinHeightChanged));

    public static double GetMinHeight(ScrollViewer scroller) => (double)scroller.GetValue(MinHeightProperty);

    public static void SetMinHeight(ScrollViewer scroller, double value) => scroller.SetValue(MinHeightProperty, value);

    private static void OnMinHeightChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ScrollViewer scroller)
            throw new ArgumentException($"{nameof(ScrollFill)}.MinHeight only works on a {nameof(ScrollViewer)}.", nameof(target));
        scroller.SizeChanged -= OnScrollerSizeChanged; // once, however often the value changes
        scroller.SizeChanged += OnScrollerSizeChanged;
        Fit(scroller);
    }

    private static void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer scroller)
            Fit(scroller);
    }

    private static void Fit(ScrollViewer scroller)
    {
        if (scroller.Content is FrameworkElement content && scroller.ActualHeight > 0)
            content.Height = Math.Max(scroller.ActualHeight, GetMinHeight(scroller));
    }
}
