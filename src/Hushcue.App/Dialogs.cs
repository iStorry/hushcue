using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue;

internal static class Dialogs
{
    public static Task InfoAsync(XamlRoot root, string message) => ShowAsync(root, Core.Product.Name, message, "OK", null);

    public static Task ErrorAsync(XamlRoot root, string message) =>
        ShowAsync(root, $"{Core.Product.Name} - error", message, "OK", null);

    /// <returns>true when the user chose <paramref name="confirm"/>.</returns>
    public static async Task<bool> ConfirmAsync(XamlRoot root, string message, string confirm) =>
        await ShowAsync(root, Core.Product.Name, message, confirm, "Cancel") == ContentDialogResult.Primary;

    private static async Task<ContentDialogResult> ShowAsync(XamlRoot root, string title, string message,
                                                             string primary, string? close)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (close is not null)
            dialog.CloseButtonText = close;
        return await dialog.ShowAsync();
    }
}
