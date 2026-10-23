using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Hushcue.Controls;

/// <summary>Lays its children out left to right and starts a new line when the next one no longer fits.</summary>
public sealed partial class WrapPanel : Panel
{
    private double _horizontalSpacing;
    private double _verticalSpacing;

    public double HorizontalSpacing
    {
        get => _horizontalSpacing;
        set
        {
            _horizontalSpacing = value;
            InvalidateMeasure();
        }
    }

    public double VerticalSpacing
    {
        get => _verticalSpacing;
        set
        {
            _verticalSpacing = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Flow(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Flow(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>The size of the lines at <paramref name="width"/>; each child is put in its place when arranging.</summary>
    private Size Flow(double width, bool arrange)
    {
        double top = 0, widest = 0;
        foreach (var line in Lines(width))
        {
            var height = line.Max(child => child.DesiredSize.Height);
            var left = 0.0;
            foreach (var child in line)
            {
                if (arrange)
                    child.Arrange(new Rect(left, top, child.DesiredSize.Width, height));
                left += child.DesiredSize.Width + HorizontalSpacing;
            }
            widest = Math.Max(widest, left - HorizontalSpacing);
            top += height + VerticalSpacing;
        }
        return new Size(widest, Math.Max(0, top - VerticalSpacing));
    }

    /// <summary>The visible children in lines no wider than <paramref name="width"/> (a child wider than that gets one of its own).</summary>
    private IEnumerable<List<UIElement>> Lines(double width)
    {
        var line = new List<UIElement>();
        var used = 0.0;
        foreach (var child in Children.Where(c => c.Visibility == Visibility.Visible))
        {
            var needed = (line.Count > 0 ? HorizontalSpacing : 0) + child.DesiredSize.Width;
            if (line.Count > 0 && used + needed > width)
            {
                yield return line;
                (line, used, needed) = ([], 0, child.DesiredSize.Width);
            }
            line.Add(child);
            used += needed;
        }
        if (line.Count > 0)
            yield return line;
    }
}
