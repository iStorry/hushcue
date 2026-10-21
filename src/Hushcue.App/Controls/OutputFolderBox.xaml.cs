using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Hushcue.Controls;

/// <summary>Where results go; empty means next to each input file.</summary>
public sealed partial class OutputFolderBox : UserControl
{
    public OutputFolderBox() => InitializeComponent();

    public string Folder
    {
        get => FolderText.Text.Trim();
        set => FolderText.Text = value;
    }

    /// <summary>The chosen folder, or null for "same folder as each input file".</summary>
    public string? FolderOrNull => Folder.Length > 0 ? Folder : null;

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        if (await picker.PickSingleFolderAsync() is { } folder)
            Folder = folder.Path;
    }
}
