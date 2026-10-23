using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Hushcue.Controls;

/// <summary>
/// One setting, as in Windows Settings: an icon, what it is, and its control (<see cref="Action"/>) on the right, or
/// under the text when the row is too narrow for both.
/// </summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class SettingRow : UserControl
{
    /// <summary>Row width (effective pixels) below which the control goes under the text.</summary>
    private const double ActionBelowWidth = 520;
    private bool _actionBelow;

    public SettingRow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PlaceAction();
    }

    public string Glyph
    {
        get => Icon.Glyph;
        set => Icon.Glyph = value;
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Description
    {
        get => DescriptionText.Text;
        set => DescriptionText.Text = value;
    }

    /// <summary>The control that changes the setting.</summary>
    public object? Action
    {
        get => ActionPresenter.Content;
        set => ActionPresenter.Content = value;
    }

    /// <summary>The control always goes under the text, at its full width (a text box).</summary>
    public bool ActionBelow
    {
        get => _actionBelow;
        set
        {
            _actionBelow = value;
            PlaceAction();
        }
    }

    private void PlaceAction()
    {
        var below = _actionBelow || (ActualWidth > 0 && ActualWidth < ActionBelowWidth);
        Grid.SetRow(ActionPresenter, below ? 1 : 0);
        Grid.SetColumn(ActionPresenter, below ? 1 : 2);
        Grid.SetColumnSpan(ActionPresenter, below ? 2 : 1);
    }
}
