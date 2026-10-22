using Hushcue.Core;
using Hushcue.Core.Media;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Pages;

/// <summary>MKV to MP4 without the subtitle tracks (lossless remux).</summary>
public sealed partial class ConvertPage : UserControl
{
    private CancellationTokenSource? _cancel;
    private Task? _job;

    public ConvertPage()
    {
        InitializeComponent();
        Queue.Extensions = [".mkv"];
        Run.StartRequested += async (_, _) => await StartAsync();
        Run.CancelRequested += (_, _) =>
        {
            _cancel?.Cancel();
            Run.SetCancelling();
        };
    }

    public Controls.FileQueue Files => Queue;

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
        _cancel = new CancellationTokenSource();
        var ui = DispatcherQueue;
        var job = Task.Run(() => Embed.ConvertFilesAsync(files, Output.FolderOrNull, SaveSrtBox.IsChecked == true, delete,
            (fraction, text) => ui.TryEnqueue(() => Run.SetProgress(fraction, text)),
            line => ui.TryEnqueue(() => Run.AppendLog(line)),
            _cancel.Token,
            onFile: file => ui.TryEnqueue(() => Queue.Update(file))));
        _job = job;
        try
        {
            var outputs = await job;
            Run.SetProgress(1, "");
            Run.SetStatus($"Done - {outputs.Count} file(s) converted", "done");
        }
        catch (OperationCanceledException)
        {
            Run.SetStatus("Cancelled");
        }
        catch (Exception e)
        {
            Run.SetStatus("Failed", "error");
            await Dialogs.ErrorAsync(XamlRoot, e.Message);
        }
        finally
        {
            Queue.ResetUnfinished();
            Run.SetRunning(false);
        }
    }
}
