using Hushcue.Core;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Pages;

/// <summary>Pick videos, get subtitles: an .srt, a subtitle track, or text burned into the picture.</summary>
public sealed partial class GeneratePage : UserControl
{
    private CancellationTokenSource? _cancel;
    private Task? _job;

    public GeneratePage()
    {
        InitializeComponent();
        Queue.Extensions = MediaFiles.Extensions;
        foreach (var preset in Presets.All)
            PresetBox.Items.Add(preset.Name);
        foreach (var (name, _) in Presets.Languages)
            LanguageBox.Items.Add(name);
        Run.StartRequested += async (_, _) => await StartAsync();
        Run.CancelRequested += (_, _) => Cancel();
    }

    public Controls.FileQueue Files => Queue;

    public bool IsRunning => _job is { IsCompleted: false };

    public void Load(AppSettings settings)
    {
        PresetBox.SelectedItem = Presets.All.Any(p => p.Name == settings.Preset) ? settings.Preset : Presets.Default;
        LanguageBox.SelectedItem = Presets.Languages.Any(l => l.Name == settings.Language) ? settings.Language : "English";
        VocabularyBox.Text = settings.Vocabulary;
        ResultBox.SelectedItem = ResultBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == settings.Embed.ToString());
        KeepSrtBox.IsChecked = settings.KeepSrt;
        DeleteBox.IsChecked = settings.DeleteOriginal;
        EnhanceBox.IsChecked = settings.Enhance;
        SkipSilenceBox.IsChecked = settings.SkipSilence;
        Output.Folder = settings.OutputFolder;
        UpdateEmbedOptions();
    }

    public void Save(AppSettings settings)
    {
        settings.Preset = (string)PresetBox.SelectedItem;
        settings.Language = (string)LanguageBox.SelectedItem;
        settings.Vocabulary = VocabularyBox.Text;
        settings.Embed = SelectedEmbed;
        settings.KeepSrt = KeepSrtBox.IsChecked == true;
        settings.DeleteOriginal = DeleteBox.IsChecked == true;
        settings.Enhance = EnhanceBox.IsChecked == true;
        settings.SkipSilence = SkipSilenceBox.IsChecked == true;
        settings.OutputFolder = Output.Folder;
    }

    /// <summary>Stop a running job (window closing); waits briefly for it to wind down.</summary>
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

    private EmbedMode SelectedEmbed =>
        Enum.Parse<EmbedMode>((string)((ComboBoxItem)ResultBox.SelectedItem).Tag);

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedItem is string name)
        {
            var preset = Presets.Get(name);
            PresetHint.Text = $"{preset.Model}: {preset.Description}";
        }
    }

    private void OnResultChanged(object sender, SelectionChangedEventArgs e) => UpdateEmbedOptions();

    private void UpdateEmbedOptions()
    {
        if (ResultBox.SelectedItem is null)
            return;
        var embedding = SelectedEmbed != EmbedMode.None;
        KeepSrtBox.IsEnabled = embedding;
        DeleteBox.IsEnabled = embedding;
        if (!embedding)
            DeleteBox.IsChecked = false;
    }

    private async Task StartAsync()
    {
        var files = Queue.Paths;
        if (files.Count == 0)
        {
            await Dialogs.InfoAsync(XamlRoot, "Add at least one video or audio file first.");
            return;
        }
        var settings = new JobSettings
        {
            Preset = (string)PresetBox.SelectedItem,
            Language = Presets.Languages.First(l => l.Name == (string)LanguageBox.SelectedItem).Code,
            Enhance = EnhanceBox.IsChecked == true,
            Vocabulary = VocabularyBox.Text,
            SkipSilence = SkipSilenceBox.IsChecked == true,
            Embed = SelectedEmbed,
            KeepSrt = KeepSrtBox.IsChecked == true,
            DeleteOriginal = DeleteBox.IsChecked == true,
        };
        if (settings.DeleteOriginal && !await Dialogs.ConfirmAsync(XamlRoot,
                "After each video is finished, its original will be moved to the Recycle Bin.\n\n"
                + $"Continue with {files.Count} file(s)?", "Continue"))
            return;

        Run.Reset();
        Run.SetStatus("Starting...");
        Run.SetRunning(true);
        _cancel = new CancellationTokenSource();
        var ui = DispatcherQueue;
        var job = SubtitleJob.RunAsync(files, Output.FolderOrNull, settings,
            (fraction, text) => ui.TryEnqueue(() => Run.SetProgress(fraction, text)),
            line => ui.TryEnqueue(() => Run.AppendLog(line)),
            src => ui.TryEnqueue(() => Queue.Remove(src)),
            _cancel.Token);
        _job = job;
        try
        {
            var outputs = await job;
            Run.SetProgress(1, "");
            Run.SetStatus($"Done - {outputs.Count} file(s) saved", "done");
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
            Run.SetRunning(false);
        }
    }

    private void Cancel()
    {
        _cancel?.Cancel();
        Run.SetCancelling();
    }
}
