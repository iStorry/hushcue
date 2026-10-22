using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue;

internal static class Dialogs
{
    // WinUI shows one ContentDialog at a time; asking for a second one while the first is open throws.
    private static bool _open;

    public static Task InfoAsync(XamlRoot root, string message) => ShowAsync(root, Core.Product.Name, message, "OK", null);

    public static Task ErrorAsync(XamlRoot root, string message) =>
        ShowAsync(root, $"{Core.Product.Name} - error", message, "OK", null);

    /// <returns>true when the user chose <paramref name="confirm"/>; false if they did not, or another dialog is open.</returns>
    public static async Task<bool> ConfirmAsync(XamlRoot root, string message, string confirm, string cancel = "Cancel") =>
        await ShowAsync(root, Core.Product.Name, message, confirm, cancel) == ContentDialogResult.Primary;

    private static async Task<ContentDialogResult> ShowAsync(XamlRoot root, string title, string message,
                                                             string primary, string? close)
    {
        if (_open)
            return ContentDialogResult.None;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        if (close is not null)
            dialog.CloseButtonText = close;
        _open = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }
}
