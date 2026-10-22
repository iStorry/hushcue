using Hushcue.Core;
using Hushcue.Core.Media;
using Hushcue.Controls;
using Hushcue.Services;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Pages;

/// <summary>MKV to MP4 without the subtitle tracks (lossless remux).</summary>
public sealed partial class ConvertPage : UserControl, IRunPage
{
    private CancellationTokenSource? _cancel;
    private Task? _job;

    public ConvertPage()
    {
        InitializeComponent();
        Queue.Extensions = [".mkv"];
        Run.SetStatus("Add .mkv files to start");
        Queue.Files.CollectionChanged += (_, _) =>
        {
            if (IsRunning)
                return;
            Run.HideProgress();
            Run.SetStatus(Queue.Files.Count == 0 ? "Add .mkv files to start"
                          : Queue.PendingPaths.Count is var n and > 0 ? $"{n} file{(n == 1 ? "" : "s")} ready" : "All files are done");
        };
        Run.StartRequested += async (_, _) => await StartAsync();
        Run.CancelRequested += async (_, ask) =>
        {
            if (_cancel is null || _cancel.IsCancellationRequested
                || (ask && !await Dialogs.ConfirmAsync(XamlRoot, "Stop converting? Files that are finished are kept.", "Stop", "Keep going")))
                return;
            _cancel?.Cancel();
            Run.SetCancelling();
        };
    }

    public event EventHandler<RunProgress>? ProgressChanged;
    public event EventHandler? Finished;

    public Controls.FileQueue Files => Queue;

    public void Start() => Run.RequestStart();

    public bool IsRunning => _job is { IsCompleted: false };

    public void Load(AppSettings settings)
    {
        SaveSrtBox.IsChecked = settings.ConvertSaveSrt;
        DeleteBox.IsChecked = settings.ConvertDeleteOriginal;
        Output.Folder = settings.ConvertOutputFolder;
    }

    public void Save(AppSettings settings)
    {
        settings.ConvertSaveSrt = SaveSrtBox.IsChecked == true;
        settings.ConvertDeleteOriginal = DeleteBox.IsChecked == true;
        settings.ConvertOutputFolder = Output.Folder;
    }

    public void Stop()
    {
        _cancel?.Cancel();
        try
        {
            _job?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // cancelled or failed: either way it has stopped
        }
    }

    private async Task StartAsync()
    {
        if (IsRunning)
            return;
        var files = Queue.PendingPaths;
        if (files.Count == 0)
        {
            await Dialogs.InfoAsync(XamlRoot, "Add at least one .mkv file first.");
            return;
        }
        var delete = DeleteBox.IsChecked == true;
        if (delete && !await Dialogs.ConfirmAsync(XamlRoot,
                "After each file is converted, its original .mkv will be moved to the Recycle Bin.\n\n"
                + $"Continue with {files.Count} file(s)?", "Continue"))
            return;

        Queue.RequeueFailed();
        Run.Reset();
        Run.SetStatus("Starting...");
        Run.SetRunning(true);
        Queue.SetLocked(true);
        _cancel = new CancellationTokenSource();
        var ui = DispatcherQueue;
        var job = Task.Run(() => Embed.ConvertFilesAsync(files, Output.FolderOrNull, SaveSrtBox.IsChecked == true, delete,
            (fraction, text) => ui.TryEnqueue(() =>
            {
                Run.SetProgress(fraction);
                if (_cancel?.IsCancellationRequested != true)
                    Run.SetStatus(text);
                ProgressChanged?.Invoke(this, new RunProgress(fraction, Taskbar.State.Normal));
            }),
            line => ui.TryEnqueue(() => Run.AppendLog(line)),
            _cancel.Token,
            onFile: file => ui.TryEnqueue(() => Queue.Update(file))));
        _job = job;
        try
        {
            var outputs = await job;
            Run.SetProgress(1);
            Run.SetStatus($"Done · {outputs.Count} file{(outputs.Count == 1 ? "" : "s")} converted", StatusKind.Done);
        }
        catch (OperationCanceledException)
        {
            Run.SetStatus("Cancelled · finished files are kept");
        }
        catch (Exception e)
        {
            Run.SetStatus("Finished with problems", StatusKind.Error);
            await Dialogs.ErrorAsync(XamlRoot, e.Message);
        }
        finally
        {
            Queue.ResetUnfinished();
            Queue.SetLocked(false);
            Run.SetRunning(false);
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }
}
