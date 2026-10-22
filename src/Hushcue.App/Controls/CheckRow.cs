using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Controls;

public enum CheckState
{
    Checking,
    Ok,
    Warning,
    Error,
}

/// <summary>One line of the setup screen: a state icon (or a spinner), what was checked, and a detail under it.</summary>
public sealed partial class CheckRow : Grid
{
    private readonly ProgressRing _ring = new() { Width = 16, Height = 16, IsActive = true };
    private readonly FontIcon _icon = new() { FontSize = 16, Visibility = Visibility.Collapsed };
    private readonly TextBlock _title = new()
    {
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _detail = new() { Style = (Style)Application.Current.Resources["Hint"], IsTextSelectionEnabled = true };

    public CheckRow()
    {
        ColumnSpacing = 12;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var state = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        state.Children.Add(_ring);
        state.Children.Add(_icon);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(_title);
        text.Children.Add(_detail);
        SetColumn(text, 1);
        Children.Add(state);
        Children.Add(text);
        // screen readers hear each check's result as it comes in
        AutomationProperties.SetLiveSetting(_title, AutomationLiveSetting.Polite);
    }

    public CheckState State { get; private set; }

    public void Set(CheckState state, string title, string detail = "")
    {
        var changed = state != State || title != _title.Text;
        State = state;
        _ring.IsActive = state == CheckState.Checking;
        _ring.Visibility = state == CheckState.Checking ? Visibility.Visible : Visibility.Collapsed;
        _icon.Visibility = state == CheckState.Checking ? Visibility.Collapsed : Visibility.Visible;
        (_icon.Glyph, _icon.Foreground) = state switch
        {
            CheckState.Ok => ("\uE930", ThemeBrushes.Get(ThemeBrushes.Success)),
            CheckState.Warning => ("\uE7BA", ThemeBrushes.Get(ThemeBrushes.Caution)),
            _ => ("\uE783", ThemeBrushes.Get(ThemeBrushes.Critical)),
        };
        _title.Text = title;
        _detail.Text = detail;
        _detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (changed && FrameworkElementAutomationPeer.FromElement(_title) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
