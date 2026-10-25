using System.Collections.ObjectModel;
using System.Diagnostics;
using Hushcue.Core;
using Hushcue.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;

namespace Hushcue.Controls;

/// <summary>
/// The files to process: a drop target with an empty state, a list showing where each file is, and add / remove
/// buttons. Finished files stay in the list, ticked, and are skipped by the next run. Optionally probes each file as
/// it is added, for its length and to warn about files a run can't do.
/// </summary>
public sealed partial class FileQueue : UserControl
{
    // ffprobe runs per added file; a few at a time keeps a big drop from starting dozens of processes
    private readonly SemaphoreSlim _probeSlots = new(3);
    private bool _locked;

    public FileQueue()
    {
        InitializeComponent();
        List.ItemsSource = Files;
        Files.CollectionChanged += (_, _) => Refresh();
        ActualThemeChanged += (_, _) =>
        {
            foreach (var file in Files)
                file.Changed(); // the rows' state colours come from the theme
        };
        Refresh();
    }

    public ObservableCollection<QueuedFile> Files { get; } = [];

    /// <summary>The files changed: added, removed, probed, or moved on in a run.</summary>
    public event EventHandler? Changed;

    /// <summary>The review button of a file (or Enter / double-click on a preview).</summary>
    public event EventHandler<QueuedFile>? ReviewRequested;

    /// <summary>The files the next run should do: everything not done yet (or a preview waiting to be kept).</summary>
    public List<QueuedFile> Pending => [.. Files.Where(f => f.State is not (FileState.Done or FileState.Review))];

    public List<string> PendingPaths => [.. Pending.Select(f => f.Path)];

    /// <summary>Extensions the file picker offers, e.g. ".mkv".</summary>
    public IReadOnlyList<string> Extensions { get; set; } = ["*"];

    /// <summary>Probe files as they are added (length, video and audio streams).</summary>
    public bool ProbeFiles { get; set; }

    /// <summary>Decides, from its probe, what a run will make of a file; null for no warnings.</summary>
    public Func<QueuedFile, FileCheck?>? Checker { get; set; }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string EmptyText
    {
        get => EmptyTitle.Text;
        set => EmptyTitle.Text = value;
    }

    /// <summary>The queued file for <paramref name="path"/>, if it is in the list.</summary>
    public QueuedFile? Get(string path) => Find(path);

    /// <summary>Add paths that are not queued yet, in order; a finished file that is added again will run again.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Find(path) is not { } queued)
            {
                var file = new QueuedFile(path);
                file.SetLocked(_locked);
                Files.Add(file);
                if (ProbeFiles)
                    file.Probing = ProbeAsync(file);
            }
            else if (queued.State is FileState.Done or FileState.Review)
            {
                queued.Requeue();
            }
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
        Refresh();
    }

    /// <summary>While a run is going: files can still be added, but not removed.</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        foreach (var file in Files)
            file.SetLocked(locked);
        Refresh();
    }

    /// <summary>Ask the probe checker again, e.g. after the result type changed.</summary>
    public void RefreshChecks()
    {
        foreach (var file in Files)
            file.SetCheck(file.State == FileState.Waiting ? Checker?.Invoke(file) : null);
        Refresh();
    }

    /// <summary>Highlight while files are dragged over the window.</summary>
    public void SetDropActive(bool active)
    {
        Outline.Stroke = ThemeBrushes.Get(active ? ThemeBrushes.Accent : ThemeBrushes.StrongStroke);
        Outline.Visibility = active || Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The Add files button and Ctrl+O.</summary>
    public async Task PickFilesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in Extensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var files = await picker.PickMultipleFilesAsync();
        AddPaths(files.Select(f => f.Path));
    }

    private async Task ProbeAsync(QueuedFile file)
    {
        await _probeSlots.WaitAsync();
        try
        {
            var info = await Task.Run(() => MediaProbe.ReadAsync(file.Path));
            file.SetProbe(info, null);
        }
        catch (Exception e) when (e is MediaException or IOException or System.Text.Json.JsonException
                                    or System.ComponentModel.Win32Exception)
        {
            file.SetProbe(null, e.Message);
        }
        finally
        {
            _probeSlots.Release();
        }
        if (file.State == FileState.Waiting)
            file.SetCheck(Checker?.Invoke(file));
        Refresh();
    }

    private QueuedFile? Find(string path) =>
        Files.FirstOrDefault(f => string.Equals(Path.GetFullPath(f.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    private void Refresh()
    {
        var any = Files.Count > 0;
        List.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Outline.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = any && !_locked;
        var count = Files.Count switch { 0 => "", 1 => "1 file", var n => $"{n} files" };
        var length = Files.Sum(f => f.Duration);
        var done = Files.Count(f => f.State == FileState.Done);
        var review = Files.Count(f => f.State == FileState.Review);
        CountText.Text = string.Join(" · ", new[]
        {
            count,
            length > 0 ? BatchEstimate.Clock(TimeSpan.FromSeconds(length)) : "",
            done > 0 ? $"{done} done" : "",
            review > 0 ? $"{review} to preview" : "",
        }.Where(p => p.Length > 0));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async void OnAdd(object sender, RoutedEventArgs e) => await PickFilesAsync();

    private void OnRemoveOne(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueuedFile file } && !_locked)
            Files.Remove(file);
    }

    private void RemoveSelected()
    {
        if (_locked)
            return;
        var index = List.SelectedIndex;
        foreach (var file in List.SelectedItems.OfType<QueuedFile>().ToList())
            Files.Remove(file);
        if (Files.Count > 0)
        {
            // keep the keyboard on the list, on the file that took the removed one's place
            List.SelectedIndex = Math.Clamp(index, 0, Files.Count - 1);
            (List.ContainerFromIndex(List.SelectedIndex) as Control)?.Focus(FocusState.Keyboard);
        }
        else
        {
            AddButton.Focus(FocusState.Keyboard);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (!_locked)
            Files.Clear();
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && List.SelectedItem is QueuedFile { State: FileState.Review, CanReview: true } review)
        {
            ReviewRequested?.Invoke(this, review);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && List.SelectedItem is QueuedFile { Output: { } output })
        {
            ShowInFolder(output);
            e.Handled = true;
        }
    }

    private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QueuedFile { State: FileState.Review, CanReview: true } review)
            ReviewRequested?.Invoke(this, review);
        else if ((e.OriginalSource as FrameworkElement)?.DataContext is QueuedFile { Output: { } output })
            ShowInFolder(output);
    }

    private void OnReview(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueuedFile file })
            ReviewRequested?.Invoke(this, file);
    }

    private void OnShowOutput(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueuedFile { Output: { } output } })
            ShowInFolder(output);
    }

    private static void ShowInFolder(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
}
