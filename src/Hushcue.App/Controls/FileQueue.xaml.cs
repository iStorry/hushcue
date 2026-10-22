using System.Collections.ObjectModel;
using System.Diagnostics;
using Hushcue.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Hushcue.Controls;

/// <summary>
/// The files to process: a drop target with an empty state, a list showing where each file is, and add/remove
/// buttons. Finished files stay in the list, ticked, and are skipped by the next run.
/// </summary>
public sealed partial class FileQueue : UserControl
{
    public FileQueue()
    {
        InitializeComponent();
        List.ItemsSource = Files;
        Files.CollectionChanged += (_, _) => Refresh();
    }

    public ObservableCollection<QueuedFile> Files { get; } = [];

    /// <summary>The files the next run should do: everything not done yet.</summary>
    public List<string> PendingPaths => [.. Files.Where(f => f.State != FileState.Done).Select(f => f.Path)];

    /// <summary>Extensions the file picker offers, e.g. ".mkv".</summary>
    public IReadOnlyList<string> Extensions { get; set; } = ["*"];

    public string EmptyText
    {
        get => EmptyTitle.Text;
        set => EmptyTitle.Text = value;
    }

    /// <summary>Add paths that are not queued yet, in order; a finished file that is added again will run again.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Find(path) is not { } queued)
                Files.Add(new QueuedFile(path));
            else if (queued.State == FileState.Done)
                queued.Requeue();
        }
        Refresh();
    }

    /// <summary>Show one file's new state from a running job.</summary>
    public void Update(FileProgress progress)
    {
        Find(progress.Path)?.Update(progress);
        Refresh();
    }

    /// <summary>A run is starting: files that failed last time get another go.</summary>
    public void RequeueFailed()
    {
        foreach (var file in Files.Where(f => f.State == FileState.Failed))
            file.Requeue();
    }

    /// <summary>The run stopped: files it was still working on go back to waiting.</summary>
    public void ResetUnfinished()
    {
        foreach (var file in Files.Where(f => f.State == FileState.Working))
            file.Requeue();
    }

    /// <summary>Highlight while files are dragged over the window.</summary>
    public void SetDropActive(bool active) =>
        Outline.Stroke = ThemeBrushes.Get(active ? ThemeBrushes.Accent : ThemeBrushes.StrongStroke);

    private QueuedFile? Find(string path) =>
        Files.FirstOrDefault(f => string.Equals(Path.GetFullPath(f.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    private void Refresh()
    {
        var any = Files.Count > 0;
        List.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        var count = Files.Count switch { 0 => "No files", 1 => "1 file", var n => $"{n} files" };
        var done = Files.Count(f => f.State == FileState.Done);
        CountText.Text = done > 0 ? $"{count} · {done} done" : count;
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
        foreach (var file in List.SelectedItems.OfType<QueuedFile>().ToList())
            Files.Remove(file);
    }

    private void OnClear(object sender, RoutedEventArgs e) => Files.Clear();

    private void OnShowOutput(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueuedFile { Output: { } output } })
            Process.Start("explorer.exe", $"/select,\"{output}\"")?.Dispose();
    }
}
