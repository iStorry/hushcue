using System.Diagnostics;
using Hushcue.Controls;
using Hushcue.Core;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Hushcue.Core.Subtitles;
using Hushcue.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Pages;

/// <summary>Pick videos, get subtitles: an .srt, a subtitle track, or text burned into the picture.</summary>
public sealed partial class GeneratePage : UserControl, IRunPage
{
    private AppSettings _settings = new();
    private string _device = "cuda";
    private CancellationTokenSource? _cancel;
    private Task? _coreJob;
    private Batch? _batch;
    private bool _showingResult;
    private bool _starting;

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
        /// <summary>The model download, while one is running: the bar and status line show it instead of the files.</summary>
        public ModelDownloading? Download { get; set; }
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
        ChoiceGroup.Attach(PresetChoices);
        ChoiceGroup.Attach(ResultChoices);
        foreach (var (name, _) in Presets.Languages)
            LanguageBox.Items.Add(name);
        Run.StartRequested += async (_, _) => await StartAsync();
        Run.CancelRequested += async (_, ask) => await CancelAsync(ask);
        Summary.Closed += (_, _) => ShowSummary(null);
        Summary.ReviewRequested += async (_, file) => await OpenReviewAsync(file);
        Queue.ReviewRequested += async (_, file) => await OpenReviewAsync(file);
        Queue.CanEdit = true;
        SizeChanged += OnPageSizeChanged;
        Queue.EditRequested += async (_, _) => await PickAndEditAsync();
        Review.Closed += (_, _) => ShowReview(false);
        Review.Embedded += (_, embedded) => Queue.Update(FileProgress.Done(embedded.File.Path, embedded.Result.Video));
        Review.IsJobRunning = () => IsRunning;
        // in a short window the options scroll: show what just opened
        MoreOptions.Expanding += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => MoreOptions.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true }));
    }

    public event EventHandler<RunProgress>? ProgressChanged;
    public event EventHandler? Finished;

    /// <summary>The run had to switch to the CPU (the GPU failed): "cpu".</summary>
    public event EventHandler<string>? DeviceChanged;

    public Controls.FileQueue Files => Queue;

    public void Start()
    {
        if (!IsReviewing)
            Run.RequestStart();
    }

    public bool IsRunning => _batch is not null;

    /// <summary>A file's subtitles are open for review (the page shows the review instead of the queue).</summary>
    public bool IsReviewing => Review.Visibility == Visibility.Visible;

    /// <summary>The review has edits that are not saved to the .srt.</summary>
    public bool HasUnsavedEdits => IsReviewing && Review.HasUnsavedEdits;

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
        ReviewBox.IsChecked = settings.ReviewBeforeEmbed;
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
        settings.ReviewBeforeEmbed = ReviewBox.IsChecked == true;
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
        Review.Shutdown();
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
        ReviewBox.IsEnabled = embedding;
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
        var finished = batch.Files.Count(f => f.State is FileState.Done or FileState.Failed or FileState.Review);
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
        FileState.Done or FileState.Failed or FileState.Review => 1,
        FileState.Working when !file.IsStarting => file.Percent / 100,
        _ => 0,
    };

    private async Task StartAsync()
    {
        if (IsRunning || _starting)
            return; // a second Ctrl+Enter while the files are read or the confirmation is open
        _starting = true;
        try
        {
            await StartBatchAsync();
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task StartBatchAsync()
    {
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
        var settings = CurrentSettings();
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
        Live.Show(null);
        ShowCard(LiveCard);
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
            ShowCard(OptionsCard);
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
            var outDir = Output.FolderOrNull;
            _coreJob = SubtitleJob.RunAsync([.. files.Select(f => f.Path)], outDir, groupSettings,
                (_, _) => { }, // the queue and the events say it all
                line => ui.TryEnqueue(() => Run.AppendLog(line)),
                file => ui.TryEnqueue(() => OnFileProgress(batch, file)),
                e => ui.TryEnqueue(() => OnJobEvent(batch, e, groupSettings, outDir)),
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

    private void OnFileProgress(Batch batch, FileProgress progress)
    {
        Queue.Update(progress);
        // the live transcript follows the file being transcribed
        if (progress.Stage == FileStage.ReadingAudio && batch.Files.FirstOrDefault(f => f.Path == progress.Path) is { } file)
            Live.Show(file);
    }

    /// <summary>What the job did, as data: the model download, the device, the subtitles as they are heard and when
    /// they are finished, recycled originals.</summary>
    private void OnJobEvent(Batch batch, JobEvent e, JobSettings settings, string? outDir)
    {
        switch (e)
        {
            case CuesDecoded decoded:
                batch.Files.FirstOrDefault(f => f.Path == decoded.Path)?.AddLiveCues(decoded.Cues);
                break;
            case ModelDownloading { Finished: false } download when _batch == batch:
                batch.Download = download;
                var fraction = download.Total > 0 ? download.Done / (double)download.Total : 0;
                Run.SetStatus($"Downloading the {download.Model} model... {(int)(fraction * 100)}% of {download.Total / 1e9:F1} GB");
                Run.SetProgress(fraction);
                ProgressChanged?.Invoke(this, new RunProgress(fraction, Taskbar.State.Normal));
                break;
            case ModelDownloading when _batch == batch:
                batch.Download = null;
                ShowRunStatus(batch);
                break;
            case DeviceSelected { Device: "cpu" } when _device != "cpu":
                _device = "cpu";
                DeviceChanged?.Invoke(this, _device);
                break;
            case FileTranscribed file:
                batch.Speed.Observe(file);
                batch.Files.FirstOrDefault(f => f.Path == file.Path)
                     ?.SetTranscript(new Transcript(file.Cues, file.Srt, settings, outDir, file.AudioSeconds));
                var name = Path.GetFileName(file.Path);
                batch.Notes.AddRange(file.Warnings.Select(warning => $"{name}: {warning}"));
                break;
            case OriginalRecycled { Moved: true } recycled:
                batch.Recycled.Add(recycled.Path);
                break;
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
        var toReview = batch.Files.Where(f => f.State == FileState.Review).ToList();
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
            if (done.Count + toReview.Count == 0)
                return;
        }
        else
        {
            var parts = new[]
            {
                done.Count > 0 || toReview.Count == 0 ? $"{done.Count} saved" : "", toReview.Count > 0 ? $"{toReview.Count} ready to review" : "",
                failed > 0 ? $"{failed} failed" : "", batch.Skipped > 0 ? $"{batch.Skipped} skipped" : "",
            };
            Run.SetStatus((failed > 0 ? "Finished · " : "Done · ") + string.Join(" · ", parts.Where(p => p.Length > 0)),
                          failed > 0 ? StatusKind.Warning : StatusKind.Done);
        }
        var notes = batch.Notes.ToList();
        notes.AddRange(Queue.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.Name}: {f.Detail}"));
        Run.SetProgress(1);
        var finished = done.Concat(toReview).ToList();
        ShowSummary(new RunSummary(done.Count, failed, batch.Skipped, TimeSpan.FromSeconds(finished.Sum(f => f.Duration)), batch.Clock.Elapsed,
                                   notes, batch.Recycled, [.. finished.Select(f => f.Output).OfType<string>()],
                                   [.. batch.Files.Where(f => f.CanReview)], toReview.Count));
    }

    /// <param name="summary">null goes back to the options</param>
    private void ShowSummary(RunSummary? summary)
    {
        if (summary is not null)
            Summary.Show(summary);
        ShowCard(summary is null ? OptionsCard : FinishedCard);
    }

    /// <summary>Page width (effective pixels) from which the live transcript and summary get a column of their own.</summary>
    private const double WideLayoutWidth = 1400;

    private Border? _card;
    private bool _wide;

    /// <summary>
    /// The right-hand side shows the options, the live transcript during a run, or the finished screen. A narrow
    /// window shows one of them in the options' place; a wide one keeps the options and adds a column for the other.
    /// </summary>
    private void ShowCard(Border card)
    {
        _card = card;
        ApplyLayout();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= WideLayoutWidth;
        if (wide == _wide)
            return;
        _wide = wide;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var card = _card ?? OptionsCard;
        var beside = _wide && card != OptionsCard;
        OptionsCard.Visibility = card == OptionsCard || beside ? Visibility.Visible : Visibility.Collapsed;
        foreach (var each in new[] { LiveCard, FinishedCard })
        {
            each.Visibility = each == card ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(each, beside ? 2 : 1);
        }
        ActivityColumn.Width = beside ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
    }

    private JobSettings CurrentSettings() => new()
    {
        Preset = SelectedPreset,
        Language = Presets.Languages.First(l => l.Name == (string)LanguageBox.SelectedItem).Code,
        Enhance = EnhanceBox.IsChecked == true,
        Vocabulary = VocabularyBox.Text,
        SkipSilence = SkipSilenceBox.IsChecked == true,
        Embed = SelectedEmbed,
        KeepSrt = KeepSrtBox.IsChecked == true,
        DeleteOriginal = DeleteBox.IsChecked == true,
        ReviewBeforeEmbed = ReviewBox.IsChecked == true,
    };

    /// <summary>"Edit subtitles": pick a video, then edit the .srt next to it (or one picked by hand).</summary>
    private async Task PickAndEditAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
        };
        foreach (var extension in MediaFiles.Extensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        if (await picker.PickSingleFileAsync() is { } video)
            await OpenInEditorAsync(video.Path, null);
    }

    /// <summary>
    /// Open a video and subtitles it already has in the editor, without transcribing: the .srt given, else the one next
    /// to the video ("name.srt" or "name.en.srt"), else one the user picks. The video joins the list as done, so it can
    /// be reopened, and the editor's Save and Embed work on that .srt.
    /// </summary>
    /// <param name="open">false only adds the video to the list, ready to review (for a second file dropped at once)</param>
    public async Task OpenInEditorAsync(string video, string? srt, bool open = true)
    {
        if (open && IsReviewing && Review.Current is not null && !await Review.CloseAsync())
            return;
        srt ??= SubtitlePairs.FindSubtitles(video) ?? await PickSubtitlesAsync(video);
        if (srt is null)
            return;
        List<Cue> cues;
        try
        {
            cues = await Srt.LoadAsync(srt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            await Dialogs.ErrorAsync(XamlRoot, $"Could not read {Path.GetFileName(srt)}: {e.Message}");
            return;
        }
        if (cues.Count == 0)
        {
            await Dialogs.InfoAsync(XamlRoot, $"{Path.GetFileName(srt)} has no subtitles in it.");
            return;
        }

        Queue.AddPaths([video]);
        if (Queue.Get(video) is not { } file)
            return;
        await file.Probing;
        if (file.ProbeError is not null)
        {
            await Dialogs.ErrorAsync(XamlRoot, $"Could not open {file.Name}: {file.ProbeError}");
            return;
        }
        file.Update(FileProgress.Done(file.Path, srt));
        file.SetTranscript(new Transcript(cues, srt, CurrentSettings(), Output.FolderOrNull,
                                          Math.Max(file.Duration, cues[^1].End)));
        if (open)
            await OpenReviewAsync(file);
    }

    private async Task<string?> PickSubtitlesAsync(string video)
    {
        if (!await Dialogs.ConfirmAsync(XamlRoot,
                $"There is no .srt next to {Path.GetFileName(video)}. Pick the subtitles to edit?", "Pick an .srt"))
            return null;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".srt");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task OpenReviewAsync(QueuedFile file)
    {
        if (!file.CanReview || ReferenceEquals(Review.Current, file) && IsReviewing)
            return;
        if (Review.Current is not null && !await Review.CloseAsync())
            return;
        ShowReview(true);
        await Review.OpenAsync(file);
    }

    private void ShowReview(bool show)
    {
        Review.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Main.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show)
            OnQueueChanged();
    }

    private void SetOptionsEnabled(bool enabled)
    {
        foreach (var control in new Control[] { MoreOptions, KeepSrtBox, DeleteBox, ReviewBox, Output })
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
