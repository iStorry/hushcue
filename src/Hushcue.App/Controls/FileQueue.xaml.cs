using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Hushcue.Controls;

/// <summary>A queued file, shown as its name over its folder.</summary>
public sealed class QueuedFile(string path)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>The files waiting to be processed: a drop target with an empty state, a list, and add/remove buttons.</summary>
public sealed partial class FileQueue : UserControl
{
    public FileQueue()
    {
        InitializeComponent();
        List.ItemsSource = Files;
        Files.CollectionChanged += (_, _) => Refresh();
    }

    public ObservableCollection<QueuedFile> Files { get; } = [];

    public List<string> Paths => [.. Files.Select(f => f.Path)];

    /// <summary>Extensions the file picker offers, e.g. ".mkv".</summary>
    public IReadOnlyList<string> Extensions { get; set; } = ["*"];

    public string EmptyText
    {
        get => EmptyTitle.Text;
        set => EmptyTitle.Text = value;
    }

    /// <summary>Add paths that are not queued yet, in order.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                Files.Add(new QueuedFile(path));
        }
    }

    /// <summary>A finished file leaves the queue, so the next run never repeats it.</summary>
    public void Remove(string path)
    {
        var match = Files.FirstOrDefault(f => string.Equals(Path.GetFullPath(f.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            Files.Remove(match);
    }

    /// <summary>Highlight while files are dragged over the window.</summary>
    public void SetDropActive(bool active) =>
        Outline.Stroke = (Brush)Application.Current.Resources[active ? "AccentFillColorDefaultBrush" : "ControlStrongStrokeColorDefaultBrush"];

    private void Refresh()
    {
        var any = Files.Count > 0;
        List.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = Files.Count switch { 0 => "No files", 1 => "1 file", var n => $"{n} files" };
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in Extensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var files = await picker.PickMultipleFilesAsync();
        AddPaths(files.Select(f => f.Path));
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var file in List.SelectedItems.Cast<QueuedFile>().ToList())
            Files.Remove(file);
    }

    private void OnClear(object sender, RoutedEventArgs e) => Files.Clear();
}
