using Microsoft.UI.Xaml;

namespace Hushcue.Controls;

/// <summary>
/// A visual state trigger that is on while <see cref="Element"/> is narrower than <see cref="Below"/> effective pixels.
/// Layouts adapt to the space a part of the page really has: AdaptiveTrigger only knows the window's width, and the
/// sidebar takes a different share of it at different sizes.
/// </summary>
public sealed partial class WidthTrigger : StateTriggerBase
{
    private FrameworkElement? _element;
    private double _below;

    public FrameworkElement? Element
    {
        get => _element;
        set
        {
            if (_element is not null)
                _element.SizeChanged -= OnSizeChanged;
            _element = value;
            if (_element is not null)
                _element.SizeChanged += OnSizeChanged;
            Update();
        }
    }

    public double Below
    {
        get => _below;
        set
        {
            _below = value;
            Update();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update();

    // not before the first layout: a width of 0 is "not measured yet", not "narrow"
    private void Update() => SetActive(_element is { ActualWidth: > 0 } element && element.ActualWidth < _below);
}
