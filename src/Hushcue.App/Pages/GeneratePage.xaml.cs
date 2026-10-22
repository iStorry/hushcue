using System.Diagnostics;
using Hushcue.Controls;
using Hushcue.Core;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Hushcue.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Pages;

/// <summary>Pick videos, get subtitles: an .srt, a subtitle track, or text burned into the picture.</summary>
public sealed partial class GeneratePage : UserControl, IRunPage
{
    // Log lines from Core's JobRun that the page follows: the file being transcribed, its warnings, recycled
    // originals, and the switch to the CPU.
    private const string FileLogPrefix = "--- ";
    private const string WarningLogPrefix = "WARNING: ";
    private const string RecycledLogPrefix = "Moved the original ";
    private static readonly string[] CpuLogMarkers = ["Falling back to CPU", "running on CPU"];

    private AppSettings _settings = new();
    private string _device = "cuda";
    private CancellationTokenSource? _cancel;
    private Task? _coreJob;
    private Batch? _batch;
    private bool _showingResult;

    /// <summary>One run over the queue, and what it did so far.</summary>
    private sealed class Batch(IReadOnlyList<QueuedFile> files, int skipped, string preset, bool burning)
    {
        public IReadOnlyList<QueuedFile> Files { get; } = files;
        public int Skipped { get; } = skipped;
        public string Preset { get; } = preset;
        /// <summary>Re-encoding takes longer than transcribing: the measured transcribing speed is no guide then.</summary>
        public bool Burning { get; } = burning;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public BatchEstimate Estimate { get; } = new();
        public SpeedMeter Speed { get; } = new();
        public List<string> Notes { get; } = [];
        public List<string> Recycled { get; } = [];
        public string? LogFile { get; set; }
        public string? Download { get; set; }
    }

    public GeneratePage()
    {
        InitializeComponent();
        Queue.Extensions = MediaFiles.Extensions;
        Queue.ProbeFiles = true;
        Queue.Checker = CheckFile;
        Queue.Changed += (_, _) => OnQueueChanged();
        Queue.Files.CollectionChanged += (_, _) =>
        {
            _showingResult = false;
            Run.HideProgress();
        };
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
        Run.CancelRequested += async (_, ask) => await CancelAsync(ask);
        Summary.Closed += (_, _) => ShowSummary(null);
    }

    public event EventHandler<RunProgress>? ProgressChanged;
    public event EventHandler? Finished;

    /// <summary>The run had to switch to the CPU (the GPU failed): "cpu".</summary>
    public event EventHandler<string>? DeviceChanged;

    public Controls.FileQueue Files => Queue;

    public bool IsRunning => _batch is not null;

    public void Load(AppSettings settings)
    {
        _settings = settings;
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
        ShowIdleStatus();
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

    /// <summary>Where transcription will run, from the system check: "cuda" or "cpu".</summary>
    public void SetDevice(string device)
    {
        _device = device;
        UpdatePresetHint();
        if (!IsRunning && !_showingResult)
            ShowIdleStatus();
    }

    /// <summary>Stop a running job (window closing); waits briefly for it to wind down.</summary>
    public void Stop()
    {
        _cancel?.Cancel();
        try
        {
            _coreJob?.Wait(TimeSpan.FromSeconds(5)); // the job runs on the thread pool, so this can't deadlock the UI
        }
        catch (AggregateException)
        {
            // cancelled or failed: either way it has stopped
        }
    }

    private string SelectedPreset => Checked<string>(PresetChoices);

    private EmbedMode SelectedEmbed => Checked<ResultChoice>(ResultChoices).Mode;

    private double? KnownSpeed(string preset) =>
        _settings.Speeds.TryGetValue(SpeedMeter.Key(preset, _device), out var speed) && speed > 0 ? speed : null;

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

    /// <summary>What a run will make of a file, from its probe: warned about in its row before the run.</summary>
    private FileCheck? CheckFile(QueuedFile file)
    {
        if (file.ProbeError is not null)
            return new FileCheck("Not a video or audio file ffmpeg can read · will be skipped", Skip: true);
        if (file.Info is not { } info)
            return null;
        if (!info.HasAudio)
            return new FileCheck("No audio stream · will be skipped", Skip: true);
        if (!info.HasVideo && SelectedEmbed != EmbedMode.None)
            return new FileCheck("No video stream · will save an .srt only", Skip: false);
        return null;
    }

    private void OnPresetChanged(object sender, RoutedEventArgs e)
    {
        UpdatePresetHint();
        if (!IsRunning && !_showingResult)
            ShowIdleStatus();
    }

    /// <summary>The preset's model and purpose, and how fast it ran on this PC last time ("~52× real time on your GPU").</summary>
    private void UpdatePresetHint()
    {
        if (PresetChoices.Children.OfType<RadioButton>().All(card => card.IsChecked != true))
            return;
        var preset = Presets.Get(SelectedPreset);
        PresetHint.Text = $"{preset.Model} · {preset.Description}";
        var speed = KnownSpeed(preset.Name);
        SpeedHint.Text = speed is { } s ? SpeedMeter.Describe(s, _device) : "";
        SpeedHint.Visibility = speed is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(SpeedHint, speed is null ? null : "Measured on this PC during your last run with this preset");
    }

    private void OnResultChanged(object sender, RoutedEventArgs e)
    {
        UpdateEmbedOptions();
        Queue.RefreshChecks();
    }

    private void UpdateEmbedOptions()
    {
        var embedding = SelectedEmbed != EmbedMode.None;
        KeepSrtBox.IsEnabled = embedding;
        DeleteBox.IsEnabled = embedding;
        if (!embedding)
            DeleteBox.IsChecked = false;
    }

    private void OnQueueChanged()
    {
        if (_batch is not null)
            ShowRunStatus(_batch);
        else if (!_showingResult)
            ShowIdleStatus();
    }

    /// <summary>"3 files ready · 12:30 of video · about 20 s on your GPU".</summary>
    private void ShowIdleStatus()
    {
        var pending = Queue.Pending;
        var usable = pending.Where(f => f.Check is not { Skip: true }).ToList();
        if (Queue.Files.Count == 0)
        {
            Run.SetStatus("Add videos or audio files to start");
            return;
        }
        if (pending.Count == 0)
        {
            Run.SetStatus("All files are done. Add more to keep going.");
            return;
        }
        var length = usable.Sum(f => f.Duration);
        var parts = new List<string> { usable.Count == 1 ? "1 file ready" : $"{usable.Count} files ready" };
        if (length > 0)
            parts.Add($"{BatchEstimate.Clock(TimeSpan.FromSeconds(length))} of {(usable.Any(f => f.Info?.HasVideo == true) ? "video" : "audio")}");
        if (length > 0 && KnownSpeed(SelectedPreset) is { } speed)
            parts.Add($"{BatchEstimate.Describe(TimeSpan.FromSeconds(length / speed))} to transcribe");
        if (pending.Count > usable.Count)
            parts.Add($"{pending.Count - usable.Count} skipped");
        Run.SetStatus(string.Join(" · ", parts));
    }

    /// <summary>"1 of 4 done · about 9 min left", the overall bar and the taskbar button.</summary>
    private void ShowRunStatus(Batch batch)
    {
        if (batch.Download is not null)
            return; // the model download reports its own progress
        // Files whose length is unknown count as the average of the others
        var known = batch.Files.Where(f => f.Duration > 0).Select(f => f.Duration).DefaultIfEmpty(60).Average();
        var work = batch.Files.Select(f => (Duration: f.Duration > 0 ? f.Duration : known, Fraction: Fraction(f))).ToList();
        var total = work.Sum(w => w.Duration);
        var overall = total > 0 ? work.Sum(w => w.Duration * w.Fraction) / total : 0;
        var finished = batch.Files.Count(f => f.State is FileState.Done or FileState.Failed);
        var left = batch.Estimate.TimeLeft(work, batch.Burning ? null : KnownSpeed(batch.Preset));
        var status = $"{finished} of {batch.Files.Count} done";
        if (_cancel?.IsCancellationRequested == true)
            status = "Cancelling...";
        else if (left is { } time && finished < batch.Files.Count)
            status += $" · {BatchEstimate.Describe(time)} left";
        else if (overall < 0.01)
            status += " · starting...";
        Run.SetStatus(status);
        Run.SetProgress(overall);
        ProgressChanged?.Invoke(this, new RunProgress(overall > 0 ? overall : null, Taskbar.State.Normal));
    }

    /// <summary>How far a file is; reading its audio and loading the model count as not started, so they don't make the
    /// first measured speed (and the time left) look far slower than the work itself.</summary>
    private static double Fraction(QueuedFile file) => file.State switch
    {
        FileState.Done or FileState.Failed => 1,
        FileState.Working when !file.IsStarting => file.Percent / 100,
        _ => 0,
    };

    private async Task StartAsync()
    {
        if (IsRunning)
            return;
        ShowSummary(null);
        Queue.RequeueFailed();
        var pending = Queue.Pending;
        if (pending.Count == 0)
        {
            await Dialogs.InfoAsync(XamlRoot, Queue.Files.Count == 0
                ? "Add at least one video or audio file first."
                : "Every file in the list is done. Add more files, or add a finished one again to redo it.");
            return;
        }
        if (pending.Any(f => !f.Probing.IsCompleted))
        {
            Run.SetStatus("Reading the files...");
            await Task.WhenAll(pending.Select(f => f.Probing));
        }
        Queue.RefreshChecks();
        var skipped = pending.Where(f => f.Check is { Skip: true }).ToList();
        var files = pending.Except(skipped).ToList();
        if (files.Count == 0)
        {
            await Dialogs.InfoAsync(XamlRoot, pending.Count == 1
                ? "This file can't be used: it isn't a video or audio file ffmpeg can read, or it has no sound."
                : "None of these files can be used: they aren't video or audio files ffmpeg can read, or they have no sound.");
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
        var srtOnly = new Func<QueuedFile, bool>(f => settings.Embed != EmbedMode.None && f.Info is { HasVideo: false });
        var recycling = settings.DeleteOriginal ? files.Count(f => !srtOnly(f)) : 0;
        if (recycling > 0 && !await Dialogs.ConfirmAsync(XamlRoot,
                $"After each video is finished, its original will be moved to the Recycle Bin. You can put "
                + $"{(recycling == 1 ? "it" : "them")} back from the finished screen.\n\nContinue with {recycling} file(s)?",
                "Continue"))
            return;

        foreach (var file in skipped)
            Queue.Update(FileProgress.Failed(file.Path, "Skipped · " + file.Check!.Warning.Replace(" · will be skipped", "")));
        // Files without a picture (while embedding) can only get an .srt: they run as their own groups, in queue order
        var groups = new List<(List<QueuedFile> Files, bool SrtOnly)>();
        foreach (var file in files)
        {
            if (groups.Count == 0 || groups[^1].SrtOnly != srtOnly(file))
                groups.Add(([], srtOnly(file)));
            groups[^1].Files.Add(file);
        }

        var batch = new Batch(files, skipped.Count, settings.Preset, settings.Embed == EmbedMode.Burn);
        _batch = batch;
        _cancel = new CancellationTokenSource();
        Run.Reset();
        Run.SetRunning(true);
        Queue.SetLocked(true);
        SetOptionsEnabled(false);
        ShowRunStatus(batch);
        Exception? failure = null;
        try
        {
            await RunGroupsAsync(batch, groups, settings, _cancel.Token);
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            batch.Clock.Stop();
            _batch = null;
            Queue.ResetUnfinished();
            Queue.SetLocked(false);
            SetOptionsEnabled(true);
            Run.SetRunning(false);
            _showingResult = true;
            RememberSpeed(batch);
            Finished?.Invoke(this, EventArgs.Empty);
        }
        await ShowOutcomeAsync(batch, failure);
    }

    private async Task RunGroupsAsync(Batch batch, List<(List<QueuedFile> Files, bool SrtOnly)> groups, JobSettings settings,
                                      CancellationToken cancel)
    {
        var ui = DispatcherQueue;
        var problems = new List<string>();
        foreach (var (files, srtOnly) in groups)
        {
            cancel.ThrowIfCancellationRequested();
            var groupSettings = srtOnly ? settings with { Embed = EmbedMode.None, KeepSrt = true, DeleteOriginal = false } : settings;
            var durations = files.ToDictionary(f => f.Path, f => f.Duration, StringComparer.OrdinalIgnoreCase);
            _coreJob = SubtitleJob.RunAsync([.. files.Select(f => f.Path)], Output.FolderOrNull, groupSettings,
                (fraction, text) => ui.TryEnqueue(() => OnJobProgress(batch, fraction, text)),
                line => ui.TryEnqueue(() => OnLog(batch, line)),
                file => ui.TryEnqueue(() =>
                {
                    batch.Speed.Observe(file, durations.GetValueOrDefault(file.Path));
                    Queue.Update(file);
                }),
                cancel);
            try
            {
                await _coreJob;
            }
            catch (MediaException e)
            {
                problems.Add(e.Message); // some videos could not be embedded; the job finished the rest
            }
        }
        if (problems.Count > 0)
            throw new MediaException(string.Join("\n\n", problems));
    }

    /// <summary>The job's overall status line: only the model download is shown from it; the queue says the rest.</summary>
    private void OnJobProgress(Batch batch, double fraction, string text)
    {
        if (_batch != batch)
            return;
        if (text.StartsWith("Downloading", StringComparison.Ordinal))
        {
            batch.Download = text;
            Run.SetStatus(text);
            Run.SetProgress(fraction);
            ProgressChanged?.Invoke(this, new RunProgress(fraction, Taskbar.State.Normal));
        }
        else if (batch.Download is not null)
        {
            batch.Download = null;
            ShowRunStatus(batch);
        }
    }

    private void OnLog(Batch batch, string line)
    {
        Run.AppendLog(line);
        if (line.StartsWith(FileLogPrefix, StringComparison.Ordinal))
        {
            batch.LogFile = line[FileLogPrefix.Length..];
        }
        else if (line.StartsWith(WarningLogPrefix, StringComparison.Ordinal))
        {
            batch.Notes.Add($"{batch.LogFile}: {line[WarningLogPrefix.Length..]}");
        }
        else if (line.StartsWith(RecycledLogPrefix, StringComparison.Ordinal)
                 && batch.Files.FirstOrDefault(f => line.StartsWith(RecycledLogPrefix + f.Name + " ", StringComparison.Ordinal)) is { } file)
        {
            batch.Recycled.Add(file.Path);
        }
        else if (CpuLogMarkers.Any(line.Contains) && _device != "cpu")
        {
            _device = "cpu";
            DeviceChanged?.Invoke(this, _device);
        }
    }

    /// <summary>Remember how fast this preset ran here, for the hint under the preset and the next batch's estimate.</summary>
    private void RememberSpeed(Batch batch)
    {
        if (batch.Speed.Speed is not { } speed)
            return;
        // half the old value: one run on a busy GPU shouldn't swing the hint too far
        var key = SpeedMeter.Key(batch.Preset, _device);
        var blended = _settings.Speeds.TryGetValue(key, out var old) && old > 0 ? (old + speed) / 2 : speed;
        _settings.Speeds[key] = Math.Round(blended, 1);
        Save(_settings);
        _settings.Save();
        UpdatePresetHint();
    }

    private async Task ShowOutcomeAsync(Batch batch, Exception? failure)
    {
        var done = batch.Files.Where(f => f.State == FileState.Done).ToList();
        var failed = batch.Files.Count(f => f.State == FileState.Failed);
        if (failure is OperationCanceledException)
        {
            Run.SetStatus(done.Count > 0 ? $"Cancelled · {done.Count} of {batch.Files.Count} done, those files are kept" : "Cancelled");
            ProgressChanged?.Invoke(this, new RunProgress(null, Taskbar.State.None));
            return;
        }
        if (failure is not null and not MediaException)
        {
            Run.SetStatus("Stopped by an error", StatusKind.Error);
            await Dialogs.ErrorAsync(XamlRoot, failure.Message);
            if (done.Count == 0)
                return;
        }
        else
        {
            var parts = new[]
            {
                $"{done.Count} saved", failed > 0 ? $"{failed} failed" : "", batch.Skipped > 0 ? $"{batch.Skipped} skipped" : "",
            };
            Run.SetStatus((failed > 0 ? "Finished · " : "Done · ") + string.Join(" · ", parts.Where(p => p.Length > 0)),
                          failed > 0 ? StatusKind.Warning : StatusKind.Done);
        }
        var notes = batch.Notes.ToList();
        notes.AddRange(Queue.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.Name}: {f.Detail}"));
        Run.SetProgress(1);
        ShowSummary(new RunSummary(done.Count, failed, batch.Skipped, TimeSpan.FromSeconds(done.Sum(f => f.Duration)), batch.Clock.Elapsed,
                                   notes, batch.Recycled, [.. done.Select(f => f.Output).OfType<string>()]));
    }

    /// <param name="summary">null goes back to the options</param>
    private void ShowSummary(RunSummary? summary)
    {
        if (summary is not null)
            Summary.Show(summary);
        FinishedCard.Visibility = summary is null ? Visibility.Collapsed : Visibility.Visible;
        OptionsCard.Visibility = summary is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetOptionsEnabled(bool enabled)
    {
        foreach (var control in new Control[] { MoreOptions, KeepSrtBox, DeleteBox, Output })
            control.IsEnabled = enabled;
        foreach (var card in PresetChoices.Children.Concat(ResultChoices.Children).OfType<Control>())
            card.IsEnabled = enabled;
        if (enabled)
            UpdateEmbedOptions();
    }

    private async Task CancelAsync(bool ask)
    {
        if (!IsRunning || _cancel is null || _cancel.IsCancellationRequested)
            return;
        if (ask && !await Dialogs.ConfirmAsync(XamlRoot,
                "Stop making subtitles? Files that are finished are kept; the file in progress is not saved.",
                "Stop", "Keep going"))
            return;
        if (!IsRunning)
            return;
        _cancel.Cancel();
        Run.SetCancelling();
    }
}
