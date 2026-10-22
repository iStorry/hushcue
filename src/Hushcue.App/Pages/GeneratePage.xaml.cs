using Hushcue.Core;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Microsoft.UI.Xaml;
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
        foreach (var (preset, column) in Presets.All.Select((p, i) => (p, i)))
        {
            var card = new RadioButton { Content = preset.Name };
            card.Checked += OnPresetChanged;
            Grid.SetColumn(card, column);
            PresetChoices.ColumnDefinitions.Add(new ColumnDefinition());
            PresetChoices.Children.Add(card);
        }
        foreach (var (name, _) in Presets.Languages)
            LanguageBox.Items.Add(name);
        Run.StartRequested += async (_, _) => await StartAsync();
        Run.CancelRequested += (_, _) => Cancel();
    }

    public Controls.FileQueue Files => Queue;

    public bool IsRunning => _job is { IsCompleted: false };

    public void Load(AppSettings settings)
    {
        var preset = Presets.Get(settings.Preset).Name;
        Check<string>(PresetChoices, name => name == preset);
        Check<ResultChoice>(ResultChoices, choice => choice.Mode == settings.Embed);
        LanguageBox.SelectedItem = Presets.Languages.Any(l => l.Name == settings.Language) ? settings.Language : "English";
        VocabularyBox.Text = settings.Vocabulary;
        KeepSrtBox.IsChecked = settings.KeepSrt;
        DeleteBox.IsChecked = settings.DeleteOriginal;
        EnhanceBox.IsChecked = settings.Enhance;
        SkipSilenceBox.IsChecked = settings.SkipSilence;
        Output.Folder = settings.OutputFolder;
        MoreOptions.IsExpanded = HasAdvancedChoices(settings);
        UpdateEmbedOptions();
    }

    public void Save(AppSettings settings)
    {
        settings.Preset = SelectedPreset;
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

    private string SelectedPreset => Checked<string>(PresetChoices);

    private EmbedMode SelectedEmbed => Checked<ResultChoice>(ResultChoices).Mode;

    /// <summary>The content of the card checked in <paramref name="group"/>.</summary>
    private static T Checked<T>(Panel group) =>
        group.Children.OfType<RadioButton>().First(card => card.IsChecked == true).Content is T content
            ? content
            : throw new InvalidOperationException($"The checked card in {group.Name} does not hold a {typeof(T).Name}.");

    private static void Check<T>(Panel group, Func<T, bool> match)
    {
        foreach (var card in group.Children.OfType<RadioButton>())
            card.IsChecked = card.Content is T content && match(content);
    }

    /// <summary>Open "More options" when something in it differs from the defaults, so it isn't forgotten.</summary>
    private static bool HasAdvancedChoices(AppSettings settings) =>
        settings.Language != new AppSettings().Language || settings.Vocabulary.Length > 0 || settings.Enhance
        || settings.SkipSilence;

    private void OnPresetChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Content: string name })
        {
            var preset = Presets.Get(name);
            PresetHint.Text = $"{preset.Model}: {preset.Description}";
        }
    }

    private void OnResultChanged(object sender, RoutedEventArgs e) => UpdateEmbedOptions();

    private void UpdateEmbedOptions()
    {
        var embedding = SelectedEmbed != EmbedMode.None;
        KeepSrtBox.IsEnabled = embedding;
        DeleteBox.IsEnabled = embedding;
        if (!embedding)
            DeleteBox.IsChecked = false;
    }

    private async Task StartAsync()
    {
        var files = Queue.PendingPaths;
        if (files.Count == 0)
        {
            await Dialogs.InfoAsync(XamlRoot, "Add at least one video or audio file first.");
            return;
        }
        var settings = new JobSettings
        {
            Preset = SelectedPreset,
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

        Queue.RequeueFailed();
        Run.Reset();
        Run.SetStatus("Starting...");
        Run.SetRunning(true);
        _cancel = new CancellationTokenSource();
        var ui = DispatcherQueue;
        var job = SubtitleJob.RunAsync(files, Output.FolderOrNull, settings,
            (fraction, text) => ui.TryEnqueue(() => Run.SetProgress(fraction, text)),
            line => ui.TryEnqueue(() => Run.AppendLog(line)),
            file => ui.TryEnqueue(() => Queue.Update(file)),
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
            Queue.ResetUnfinished();
            Run.SetRunning(false);
        }
    }

    private void Cancel()
    {
        _cancel?.Cancel();
        Run.SetCancelling();
    }
}
