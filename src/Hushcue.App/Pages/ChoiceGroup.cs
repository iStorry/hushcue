using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Hushcue.Pages;

/// <summary>
/// Keyboard behaviour of a radio group built from cards, as the RadioButtons control has: Tab enters the group on the
/// checked card, and the arrow keys move to the previous or next card (wrapping round) and check it.
/// </summary>
internal static class ChoiceGroup
{
    /// <param name="group">a panel of RadioButtons with TabFocusNavigation Once</param>
    public static void Attach(Panel group)
    {
        group.GettingFocus += (_, args) =>
        {
            var isTab = args.Direction is FocusNavigationDirection.Next or FocusNavigationDirection.Previous;
            var cameFromOutside = args.OldFocusedElement is not DependencyObject old || !IsInside(old, group);
            if (isTab && cameFromOutside && Checked(group) is { } card && !ReferenceEquals(args.NewFocusedElement, card))
                args.TrySetNewFocusedElement(card);
        };
        // Before the cards see the key: RadioButton's own arrow handling does not work across this layout
        group.PreviewKeyDown += (_, e) =>
        {
            var step = e.Key switch
            {
                VirtualKey.Left or VirtualKey.Up => -1,
                VirtualKey.Right or VirtualKey.Down => 1,
                _ => 0,
            };
            var cards = group.Children.OfType<RadioButton>().Where(c => c.IsEnabled && c.Visibility == Visibility.Visible).ToList();
            var current = cards.FindIndex(c => c.FocusState != FocusState.Unfocused);
            if (step == 0 || current < 0 || cards.Count < 2)
                return;
            var next = cards[(current + step + cards.Count) % cards.Count];
            next.Focus(FocusState.Keyboard);
            next.IsChecked = true;
            e.Handled = true;
        };
    }

    private static RadioButton? Checked(Panel group) =>
        group.Children.OfType<RadioButton>().FirstOrDefault(card => card.IsChecked == true);

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, container))
                return true;
        }
        return false;
    }
}
