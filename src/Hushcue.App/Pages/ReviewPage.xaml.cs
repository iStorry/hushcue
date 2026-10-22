using System.Collections.ObjectModel;
using System.Diagnostics;
using Hushcue.Controls;
using Hushcue.Core;
using Hushcue.Core.Engine;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Hushcue.Core.Review;
using Hushcue.Core.Subtitles;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Hushcue.Pages;

/// <summary>A reviewed file's subtitles were put into its video.</summary>
public sealed record ReviewEmbedded(QueuedFile File, EmbedResult Result);

/// <summary>
/// Review and edit one file's subtitles before they go into the video: the video with the subtitle drawn over it, a
/// waveform timeline whose subtitle blocks can be dragged, and the list of subtitles to search, fix, split, merge,
/// delete, add and re-run, with the flagged ones to check. The editing itself is Core's <see cref="CueEditor"/>; this
/// page shows it and turns clicks and keys into its edits.
/// </summary>
public sealed partial class ReviewPage : UserControl
{
    private const double StepSeconds = 5;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly MediaPlayer _player = new() { AutoPlay = false };
    private ObservableCollection<ReviewRow> _rows = [];
    private QueuedFile? _file;
    private Transcript? _transcript;
    private CueEditor? _editor;
    private MediaSource? _media;
    private float[]? _audio;
    private CancellationTokenSource? _reading;
    private CancellationTokenSource? _embedding;
    private string? _srtPath;
    private string? _output;
    private string? _recycled;
    private int _editing = -1;
    private int _current = -1;
    private bool _syncing;
    private bool _busy;

    public ReviewPage()
    {
        InitializeComponent();
        _player.CommandManager.IsEnabled = false; // a preview: the keyboard's media keys belong to the user's player
        Player.SetMediaPlayer(_player);
        _player.MediaFailed += (_, e) => DispatcherQueue.TryEnqueue(() =>
            VideoMessage.Text = $"This file can't be previewed here ({e.ErrorMessage.Trim()}). The subtitles can still be edited.");
        CueList.ItemsSource = _rows;
        _clock.Tick += (_, _) => OnClock();
        Timeline.SeekRequested += (_, seconds) => Seek(seconds);
        Timeline.CueClicked += (_, index) => Select(RowOf(index), seek: true);
        Timeline.TimesChanged += (_, e) => _editor?.SetTimes(e.Index, e.Start, e.End);
        ActualThemeChanged += (_, _) =>
        {
            foreach (var row in _rows)
                row.Changed();
        };
    }

    /// <summary>Back to the files.</summary>
    public event EventHandler? Closed;

    public event EventHandler<ReviewEmbedded>? Embedded;

    /// <summary>Whether a batch is running: re-running a part needs the model to itself.</summary>
    public Func<bool> IsJobRunning { get; set; } = () => false;

    /// <summary>The file being reviewed, or null.</summary>
    public QueuedFile? Current => _file;

    /// <summary>Edits not saved to the .srt yet (also typing not made into an edit).</summary>
    public bool HasUnsavedEdits => _editor is not null && (_editor.IsDirty || HasTypedChange(TextEditor) || HasTypedChange(StartBox)
                                                           || HasTypedChange(EndBox));

    private int Count => _editor?.Cues.Count ?? 0;

    private double Position => _player.PlaybackSession.Position.TotalSeconds;

    private ReviewRow? SelectedRow => CueList.SelectedItem as ReviewRow;

    /// <summary>The selected subtitles' indices, in order.</summary>
    private List<int> SelectedCues => [.. CueList.SelectedItems.OfType<ReviewRow>().Where(r => !r.IsGap).Select(r => r.Cue).Order()];

    public async Task OpenAsync(QueuedFile file)
    {
        var transcript = file.Transcript ?? throw new InvalidOperationException($"{file.Name} has no subtitles to review yet.");
        (_file, _transcript, _output, _recycled, _audio) = (file, transcript, null, null, null);
        IReadOnlyList<Cue> cues = transcript.Cues;
        if (transcript.Srt is { } srt && File.Exists(srt))
        {
            try
            {
                cues = await Srt.LoadAsync(srt); // what was saved, also if it was edited elsewhere
            }
            catch (IOException)
            {
                // the transcript in memory will do
            }
        }
        _srtPath = transcript.Srt;
        _editor = new CueEditor(cues, Math.Max(transcript.Duration, file.Duration));
        _editor.Changed += OnEdited;

        FileTitle.Text = file.Name;
        ModeBox.SelectedIndex = transcript.Settings.Embed == EmbedMode.Burn ? 1 : 0;
        ResultLinks.Visibility = Visibility.Collapsed;
        EmbedBar.Visibility = Visibility.Collapsed;
        SearchBox.Text = "";
        Timeline.SetSource(_editor, _editor.Duration);
        Timeline.SetWaveform(null, "Reading the audio...");
        OpenMedia(file.Path, file.Info?.HasVideo != false);
        RebuildRows();
        ShowSaveState();
        _clock.Start();
        _ = ReadAudioAsync(file.Path, transcript.Settings);

        // start at the beginning; the flags button goes through what to check
        Select(_rows.FirstOrDefault(r => !r.IsGap), seek: false);
        UpdateAll();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusSelectedRow);
    }

    /// <summary>Leave the page, asking to save unsaved edits first.</summary>
    /// <returns>false when it stays open (busy, or the save failed)</returns>
    public async Task<bool> CloseAsync()
    {
        if (_busy || _editor is null)
            return _editor is null;
        CommitPending();
        if (_editor.IsDirty && await Dialogs.ConfirmAsync(XamlRoot, $"Save your changes to the subtitles of {_file?.Name}?", "Save", "Don't save")
            && !await SaveAsync())
            return false;
        Shutdown();
        Closed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Stop playing, reading and embedding, and let go of the file (the window is closing).</summary>
    public void Shutdown()
    {
        _clock.Stop();
        _reading?.Cancel();
        _embedding?.Cancel();
        ReleaseMedia();
        _audio = null;
        _editor = null;
        _file = null;
        _rows = [];
        CueList.ItemsSource = _rows;
        Timeline.SetWaveform(null);
    }

    // --- media

    private async void OpenMedia(string path, bool hasVideo)
    {
        ReleaseMedia();
        VideoMessage.Text = hasVideo ? "" : "Audio only: there is no picture to preview.";
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (_file is null)
                return; // closed meanwhile
            _media = MediaSource.CreateFromStorageFile(file);
            _player.Source = _media;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            VideoMessage.Text = $"The video can't be opened for a preview ({e.Message}). The subtitles can still be edited.";
        }
    }

    /// <summary>Close the video file, so it can be moved to the Recycle Bin.</summary>
    private void ReleaseMedia()
    {
        _player.Pause();
        _player.Source = null;
        _media?.Dispose();
        _media = null;
    }

    private async Task ReadAudioAsync(string path, JobSettings settings)
    {
        _reading?.Cancel();
        var reading = _reading = new CancellationTokenSource();
        try
        {
            var enhance = settings.Enhance || Presets.Get(settings.Preset).Enhance; // as the run heard it, for re-runs
            var (audio, wave) = await Task.Run(async () =>
            {
                var samples = await Audio.ReadAsync(path, enhance, reading.Token);
                return (samples, Waveform.Build(samples, Audio.SampleRate));
            }, reading.Token);
            if (reading.IsCancellationRequested || _editor is null)
                return;
            _audio = audio;
            _editor.Duration = Math.Max(_editor.Duration, wave.Duration);
            Timeline.SetWaveform(wave);
            UpdateAll();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is MediaException or IOException or System.ComponentModel.Win32Exception)
        {
            Timeline.SetWaveform(null, "The audio could not be read, so there is no waveform and no re-run: " + e.Message);
        }
    }

    private void OnClock()
    {
        if (_editor is null)
            return;
        var session = _player.PlaybackSession;
        var playing = session.PlaybackState == MediaPlaybackState.Playing;
        var position = Position;
        Timeline.SetPosition(position, follow: playing);
        TimeText.Text = $"{CueTime.Format(position)} / {CueTime.Format(_editor.Duration)}";
        var index = _editor.IndexAt(position);
        OverlayText.Text = index >= 0 ? Cleanup.WrapLines(_editor.Cues[index].Text) : "";
        Overlay.Visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (index != _current)
        {
            _current = index;
            foreach (var row in _rows)
                row.IsCurrent = !row.IsGap && row.Cue == index;
        }
        var glyph = playing ? "" : "";
        if (PlayIcon.Glyph != glyph)
        {
            PlayIcon.Glyph = glyph;
            AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");
            ToolTipService.SetToolTip(PlayButton, playing ? "Pause (Space or K)" : "Play (Space or K)");
        }
    }

    private void TogglePlay()
    {
        if (_player.Source is null)
            return;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            _player.Pause();
        else
            _player.Play();
    }

    private void Seek(double seconds)
    {
        if (_editor is null)
            return;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _editor.Duration));
        OnClock();
    }

    // --- the list

    /// <summary>The rows for the cues (only those matching the search) and, without a search, the stretches to check.</summary>
    private List<ReviewRow> BuildRows()
    {
        if (_editor is null)
            return [];
        var flags = _editor.Flags;
        var byCue = flags.Where(f => f.Cue is not null).GroupBy(f => f.Cue!.Value).ToDictionary(g => g.Key, g => g.First());
        var query = SearchBox.Text.Trim();
        var stretches = query.Length == 0 ? flags.Where(f => f.Cue is null).ToList() : [];
        var rows = new List<ReviewRow>();
        var next = 0;
        foreach (var i in _editor.Search(query))
        {
            while (next < stretches.Count && stretches[next].Start <= _editor.Cues[i].Start)
                rows.Add(ReviewRow.ForGap(stretches[next++]));
            rows.Add(ReviewRow.ForCue(i, _editor.Cues[i], byCue.GetValueOrDefault(i)));
        }
        rows.AddRange(stretches.Skip(next).Select(ReviewRow.ForGap));
        return rows;
    }

    private void RebuildRows()
    {
        var rows = BuildRows();
        _syncing = true;
        try
        {
            if (rows.Count == _rows.Count)
            {
                // same shape (a text or timing edit): update in place, so the list keeps its place
                for (var i = 0; i < rows.Count; i++)
                    _rows[i].CopyFrom(rows[i]);
            }
            else
            {
                _rows = [.. rows];
                CueList.ItemsSource = _rows;
            }
            foreach (var row in _rows)
                row.IsCurrent = !row.IsGap && row.Cue == _current;
        }
        finally
        {
            _syncing = false;
        }
    }

    private ReviewRow? RowOf(int cue) => cue < 0 ? null : _rows.FirstOrDefault(r => r.Cue == cue);

    private ReviewRow? RowOf(CueFlag flag) =>
        flag.Cue is { } cue ? RowOf(cue) : _rows.FirstOrDefault(r => r.IsGap && r.Start == flag.Start && r.Flag?.Kind == flag.Kind);

    /// <param name="seek">play from the row's start too (a choice by the user, not an edit)</param>
    private void Select(ReviewRow? row, bool seek)
    {
        if (row is not null && !_rows.Contains(row))
            row = null;
        var same = ReferenceEquals(CueList.SelectedItem, row) && CueList.SelectedItems.Count <= 1;
        _syncing = true;
        try
        {
            if (!same)
            {
                CueList.SelectedItems.Clear();
                if (row is not null)
                    CueList.SelectedItem = row;
            }
        }
        finally
        {
            _syncing = false;
        }
        if (row is not null)
        {
            CueList.ScrollIntoView(row);
            if (seek)
            {
                Seek(row.Start);
                Timeline.Reveal(row.Start, row.End);
            }
        }
        AfterSelection();
    }

    private void OnRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;
        if (e.AddedItems.Count == 1 && e.AddedItems[0] is ReviewRow added && CueList.SelectedItems.Count == 1)
        {
            Seek(added.Start);
            Timeline.Reveal(added.Start, added.End);
        }
        AfterSelection();
    }

    private void AfterSelection()
    {
        LoadEditor();
        Timeline.SetSelected(SelectedCues is [var only] ? only : -1);
        UpdateCommands();
    }

    private void FocusSelectedRow()
    {
        if (SelectedRow is { } row && CueList.ContainerFromItem(row) is ListViewItem item)
            item.Focus(FocusState.Keyboard);
        else
            CueList.Focus(FocusState.Programmatic);
    }

    /// <summary>Up / Down: the row before or after the selected one (or the one playing).</summary>
    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
            return;
        var from = SelectedRow is { } row ? _rows.IndexOf(row) : RowOf(_editor?.IndexFrom(Position) ?? -1) is { } playing ? _rows.IndexOf(playing) - delta : -1;
        Select(_rows[Math.Clamp(from + delta, 0, _rows.Count - 1)], seek: true);
        FocusSelectedRow();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_editor is null)
            return;
        var selected = SelectedRow?.Cue ?? -1;
        RebuildRows();
        Select(RowOf(selected), seek: false);
        var query = SearchBox.Text.Trim();
        StatusText.Text = query.Length == 0 ? StatusText.Text
            : _rows.Count switch { 0 => $"No subtitles contain \"{query}\"", 1 => "1 subtitle matches", var n => $"{n} subtitles match" };
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _rows.Count > 0)
        {
            Select(SelectedRow ?? _rows[0], seek: true);
            FocusSelectedRow();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
    }

    // --- flags

    private void GoToFlag(int direction)
    {
        if (_editor?.Flags is not { Count: > 0 } flags)
            return;
        if (SearchBox.Text.Length > 0)
            SearchBox.Text = ""; // stretches and other rows show again
        var from = SelectedRow?.Start ?? Position;
        var flag = direction > 0
            ? flags.FirstOrDefault(f => f.Start > from + 0.001) ?? flags[0]
            : flags.LastOrDefault(f => f.Start < from - 0.001) ?? flags[^1];
        Select(RowOf(flag), seek: true);
        FocusSelectedRow();
    }

    private void OnNextFlag(object sender, RoutedEventArgs e) => GoToFlag(1);

    private void OnPreviousFlag(object sender, RoutedEventArgs e) => GoToFlag(-1);

    private void OnDismissFlag(object sender, RoutedEventArgs e)
    {
        if (SelectedRow?.Flag is { } flag)
            _editor?.Dismiss(flag);
    }

    // --- the editor fields

    private void LoadEditor()
    {
        var cues = SelectedCues;
        var row = SelectedRow;
        _editing = cues is [var only] && _editor is not null ? only : -1;
        if (_editing >= 0)
        {
            var cue = _editor!.Cues[_editing];
            EditorTitle.Text = $"SUBTITLE {_editing + 1} OF {Count}";
            TextEditor.Text = cue.Text;
            StartBox.Text = CueTime.Format(cue.Start, 3);
            EndBox.Text = CueTime.Format(cue.End, 3);
        }
        else
        {
            EditorTitle.Text = row is { IsGap: true } ? $"NO SUBTITLES · {CueTime.Range(row.Start, row.End)}"
                : cues.Count > 1 ? $"{cues.Count} SUBTITLES SELECTED"
                : "NOTHING SELECTED";
            TextEditor.Text = "";
            StartBox.Text = EndBox.Text = "";
        }
        var flag = row?.Flag;
        FlagPanel.Visibility = flag is null ? Visibility.Collapsed : Visibility.Visible;
        FlagMessage.Text = flag?.Message ?? "";
    }

    /// <summary>Save what is typed in the text and time boxes as edits (before saving, leaving, or another edit).</summary>
    private void CommitPending()
    {
        CommitText();
        CommitTimes();
    }

    private void CommitText()
    {
        if (_editor is null || _editing < 0 || _editing >= Count)
            return;
        if (TextEditor.Text != _editor.Cues[_editing].Text && !_editor.SetText(_editing, TextEditor.Text))
            TextEditor.Text = _editor.Cues[_editing].Text;
    }

    private void CommitTimes()
    {
        if (_editor is null || _editing < 0 || _editing >= Count)
            return;
        var cue = _editor.Cues[_editing];
        if (StartBox.Text == CueTime.Format(cue.Start, 3) && EndBox.Text == CueTime.Format(cue.End, 3))
            return;
        if (!CueTime.TryParse(StartBox.Text, out var start) || !CueTime.TryParse(EndBox.Text, out var end))
        {
            StatusText.Text = "Times are minutes and seconds, like 04:12.3";
            LoadEditor();
            return;
        }
        if (!_editor.SetTimes(_editing, start, end))
            LoadEditor(); // shows where the times were kept
    }

    private void OnTextKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitText();
            FocusSelectedRow();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && _editing >= 0 && _editor is not null)
        {
            TextEditor.Text = _editor.Cues[_editing].Text;
            FocusSelectedRow();
            e.Handled = true;
        }
    }

    private void OnTextLostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void OnTimeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitTimes();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            LoadEditor();
            e.Handled = true;
        }
    }

    private void OnTimeLostFocus(object sender, RoutedEventArgs e) => CommitTimes();

    // --- edits

    private void OnEdited(object? sender, CueEdit edit)
    {
        RebuildRows();
        Select(RowOf(edit.Select), seek: false);
        Timeline.RefreshCues();
        if (_editor is not null && edit.Select >= 0 && edit.Select < Count)
            Timeline.Reveal(_editor.Cues[edit.Select].Start, _editor.Cues[edit.Select].End);
        UpdateAll();
        if (edit.Undone)
            StatusText.Text = $"{(_editor?.RedoName == edit.Name ? "Undid" : "Redid")} {edit.Name.ToLowerInvariant()}";
    }

    private void OnSplit(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _editing < 0)
            return;
        CommitText();
        // timed by where the text divides; the playhead sits at the cue's start after selecting it
        if (_editor.Split(_editing, TextEditor.SelectionStart) < 0)
            StatusText.Text = "A subtitle of one word, or this short, can't be split";
    }

    private void OnMerge(object sender, RoutedEventArgs e)
    {
        if (_editor is not null && _editing >= 0)
        {
            CommitText();
            _editor.Merge(_editing);
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e) => AddAtPlayhead();

    private void AddAtPlayhead()
    {
        if (_editor is null)
            return;
        CommitPending();
        if (_editor.Insert(Position, "New subtitle") < 0)
        {
            StatusText.Text = "No room for a new subtitle here: move the playhead to a gap between subtitles";
            return;
        }
        TextEditor.Focus(FocusState.Keyboard);
        TextEditor.SelectAll();
    }

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_editor is null || SelectedCues is not { Count: > 0 } cues)
            return;
        if (cues[^1] - cues[0] + 1 == cues.Count)
        {
            _editor.Delete(cues[0], cues.Count);
            return;
        }
        foreach (var cue in Enumerable.Reverse(cues))
            _editor.Delete(cue);
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CommitPending();
        _editor?.Undo();
    }

    private void Redo()
    {
        CommitPending();
        _editor?.Redo();
    }

    // --- re-run a part

    /// <summary>The stretch "Re-run this part" would transcribe: the selected subtitles, or the selected gap.</summary>
    private (double Start, double End)? RerunRange()
    {
        if (_editor is null)
            return null;
        if (SelectedRow is { IsGap: true } gap && CueList.SelectedItems.Count == 1)
            return (gap.Start, gap.End);
        return SelectedCues is { Count: > 0 } cues ? (_editor.Cues[cues[0]].Start, _editor.Cues[cues[^1]].End) : null;
    }

    private async void OnRerun(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _transcript is null || _audio is null || RerunRange() is not var (start, end))
            return;
        if (IsJobRunning())
        {
            StatusText.Text = "Re-running a part needs the speech model, which the files being transcribed are using. Try again when they are done.";
            return;
        }
        CommitPending();
        var range = CueTime.Range(start, end);
        SetBusy(true);
        StatusText.Text = $"Re-running {range}...";
        try
        {
            var editor = _editor;
            var cues = await PartTranscription.RunAsync(_audio, start, end, _transcript.Settings, _ => { });
            if (editor != _editor)
                return; // the page moved on
            if (cues.Count == 0)
            {
                StatusText.Text = $"Nothing was heard in {range}; the subtitles are unchanged.";
                return;
            }
            editor.ReplaceRange(start, end, cues, $"Re-run {range}");
            StatusText.Text = $"Re-ran {range}: {cues.Count} subtitle{(cues.Count == 1 ? "" : "s")}. Ctrl+Z puts the old ones back.";
        }
        catch (Exception ex) when (ex is EngineException or MediaException or InvalidOperationException)
        {
            StatusText.Text = "Could not re-run this part: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // --- save, export, embed

    private string DefaultSrtPath()
    {
        var source = _file!.Path;
        var folder = _transcript?.OutDir ?? Path.GetDirectoryName(source) ?? "";
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + ".srt");
    }

    private void ShowSaveState()
    {
        if (_editor is null || _file is null)
            return;
        var name = Path.GetFileName(_srtPath ?? DefaultSrtPath());
        StatusText.Text = _editor.IsDirty ? $"Unsaved changes · Save writes {name}" : $"Edits save to {name}";
    }

    private async Task<bool> SaveAsync()
    {
        if (_editor is null || _file is null || _transcript is null)
            return false;
        CommitPending();
        var path = _srtPath ?? FileNames.Unique(DefaultSrtPath());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await Srt.SaveAsync(path, _editor.Cues);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save the subtitles: " + e.Message;
            return false;
        }
        _srtPath = path;
        _editor.MarkSaved();
        _transcript = _transcript with { Cues = [.. _editor.Cues], Srt = path };
        _file.SetTranscript(_transcript);
        StatusText.Text = $"Saved {Path.GetFileName(path)}";
        UpdateCommands();
        return true;
    }

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _file is null)
            return;
        CommitPending();
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(_file.Path), DefaultFileExtension = ".srt" };
        picker.FileTypeChoices.Add("SubRip subtitles", [".srt"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var target = await picker.PickSaveFileAsync();
        if (target is null)
            return;
        try
        {
            await Srt.SaveAsync(target.Path, _editor.Cues);
            StatusText.Text = $"Exported {target.Name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not export the subtitles: " + ex.Message;
        }
    }

    private async void OnEmbed(object sender, RoutedEventArgs e)
    {
        if (_editor is not { Cues.Count: > 0 } editor || _file is not { } file || _transcript is null)
            return;
        CommitPending();
        if (editor.IsDirty && !await SaveAsync())
            return;
        var mode = ModeBox.SelectedIndex == 1 ? EmbedMode.Burn : EmbedMode.Soft;
        // the run asked for the original to go to the Recycle Bin once its video is made
        var recycle = _transcript.Settings is { DeleteOriginal: true, Embed: not EmbedMode.None };
        var cues = editor.Cues.ToList();
        var embedding = _embedding = new CancellationTokenSource();
        SetBusy(true, embedding: true);
        StatusText.Text = mode == EmbedMode.Soft ? "Adding the subtitle track..." : "Burning the subtitles into the video...";
        if (recycle)
            ReleaseMedia(); // the preview holds the file open
        var ui = DispatcherQueue;
        try
        {
            var result = await Task.Run(() => SubtitleJob.EmbedAsync(file.Path, cues, _transcript.OutDir, mode, recycle,
                f => ui.TryEnqueue(() => EmbedBar.Value = f * 1000), _ => { }, embedding.Token));
            _output = result.Video;
            _recycled = result.Recycled ? file.Path : null;
            StatusText.Text = $"Saved {Path.GetFileName(result.Video)}"
                              + (result.Recycled ? " · the original was moved to the Recycle Bin" : "");
            ResultLinks.Visibility = Visibility.Visible;
            RestoreLink.Visibility = result.Recycled ? Visibility.Visible : Visibility.Collapsed;
            Embedded?.Invoke(this, new ReviewEmbedded(file, result));
            if (result.Recycled && _file == file)
                OpenMedia(result.Video, hasVideo: true);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Embedding cancelled; the video was not changed";
        }
        catch (Exception ex) when (ex is MediaException or IOException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Could not add the subtitles to the video: " + ex.Message;
        }
        finally
        {
            if (recycle && _player.Source is null && _file == file && File.Exists(file.Path))
                OpenMedia(file.Path, hasVideo: true);
            SetBusy(false);
        }
    }

    private void OnCancelEmbed(object sender, RoutedEventArgs e) => _embedding?.Cancel();

    private void OnShowOutput(object sender, RoutedEventArgs e)
    {
        if (_output is not null)
            Process.Start("explorer.exe", $"/select,\"{_output}\"")?.Dispose();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_recycled is null)
            return;
        StatusText.Text = RecycleBinRestore.Restore(_recycled)
            ? "The original is back where it was"
            : "Could not put the original back: it is no longer in the Recycle Bin, or a file with its name is in the way";
        RestoreLink.Visibility = Visibility.Collapsed;
        _recycled = null;
    }

    // --- state of the buttons

    private void SetBusy(bool busy, bool embedding = false)
    {
        _busy = busy;
        EmbedBar.Value = 0;
        EmbedBar.Visibility = busy && embedding ? Visibility.Visible : Visibility.Collapsed;
        CancelEmbedButton.Visibility = busy && embedding ? Visibility.Visible : Visibility.Collapsed;
        EmbedButton.Visibility = busy && embedding ? Visibility.Collapsed : Visibility.Visible;
        if (busy)
            ResultLinks.Visibility = Visibility.Collapsed;
        UpdateCommands();
        if (busy && embedding)
            CancelEmbedButton.Focus(FocusState.Programmatic);
    }

    private void UpdateAll()
    {
        if (_editor is null)
            return;
        var flags = _editor.Flags.Count;
        FlagsText.Text = flags switch { 0 => "Nothing to check", 1 => "1 to review", _ => $"{flags} to review" };
        AutomationProperties.SetName(FlagsButton, flags == 0 ? "Nothing flagged to check" : $"{FlagsText.Text}: go to the next one");
        FlagsButton.IsEnabled = PreviousFlagButton.IsEnabled = flags > 0;
        FileFacts.Text = $"{Count} subtitles · {BatchEstimate.Clock(TimeSpan.FromSeconds(_editor.Duration))}";
        if (!_busy)
            ShowSaveState();
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        if (_editor is null)
            return;
        var idle = !_busy;
        var cues = SelectedCues;
        var one = cues.Count == 1;
        SplitButton.IsEnabled = idle && one;
        MergeButton.IsEnabled = idle && one && cues[0] + 1 < Count;
        DeleteButton.IsEnabled = idle && cues.Count > 0;
        AddButton.IsEnabled = idle;
        TextEditor.IsEnabled = StartBox.IsEnabled = EndBox.IsEnabled = idle && one;
        RerunButton.IsEnabled = idle && _audio is not null && RerunRange() is not null;
        ToolTipService.SetToolTip(RerunButton, _audio is null
            ? "Available once the audio has been read"
            : "Transcribe the selected stretch again and replace its subtitles (Ctrl+Z puts them back)");
        UndoButton.IsEnabled = idle && _editor.CanUndo;
        RedoButton.IsEnabled = idle && _editor.CanRedo;
        var undo = _editor.UndoName is { } u ? $"Undo {u.ToLowerInvariant()} (Ctrl+Z)" : "Undo (Ctrl+Z)";
        var redo = _editor.RedoName is { } r ? $"Redo {r.ToLowerInvariant()} (Ctrl+Y)" : "Redo (Ctrl+Y)";
        ToolTipService.SetToolTip(UndoButton, undo);
        ToolTipService.SetToolTip(RedoButton, redo);
        AutomationProperties.SetName(UndoButton, undo);
        AutomationProperties.SetName(RedoButton, redo);
        SaveButton.IsEnabled = ExportButton.IsEnabled = idle;
        var canEmbed = _file?.Info?.HasVideo != false && _file is not null && File.Exists(_file.Path) && Count > 0;
        EmbedButton.IsEnabled = ModeBox.IsEnabled = idle && canEmbed;
        ToolTipService.SetToolTip(EmbedButton, canEmbed
            ? "Save the subtitles and put them into a copy of the video"
            : "There is no video to put the subtitles into (an audio file, or the original was moved)");
        BackButton.IsEnabled = idle;
    }

    // --- keyboard

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Space / K play and pause, J / L step 5 s, Up / Down move between subtitles, Enter edits, Delete deletes, F8 goes
    /// to the next flag; Ctrl+Z / Ctrl+Y undo and redo, Ctrl+S saves, Ctrl+F finds. Keys a focused text box or button
    /// needs are left to it.
    /// </summary>
    private async void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editor is null)
            return;
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        var inText = focused is TextBox;
        var onButton = focused is ButtonBase or ComboBox or Microsoft.UI.Xaml.Controls.ComboBoxItem;
        if (IsDown(VirtualKey.Control))
        {
            switch (e.Key)
            {
                case VirtualKey.S:
                    e.Handled = true;
                    if (!_busy)
                        await SaveAsync();
                    break;
                case VirtualKey.F:
                    e.Handled = true;
                    SearchBox.Focus(FocusState.Keyboard);
                    SearchBox.SelectAll();
                    break;
                case VirtualKey.Z or VirtualKey.Y when !_busy && !(inText && HasTypedChange(focused)):
                    e.Handled = true;
                    if (e.Key == VirtualKey.Z)
                        Undo();
                    else
                        Redo();
                    break;
            }
            return;
        }
        if (e.Key == VirtualKey.F8)
        {
            GoToFlag(IsDown(VirtualKey.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (inText)
            return;
        switch (e.Key)
        {
            case VirtualKey.Space when !onButton:
            case VirtualKey.K:
                TogglePlay();
                break;
            case VirtualKey.J:
                Seek(Position - StepSeconds);
                break;
            case VirtualKey.L:
                Seek(Position + StepSeconds);
                break;
            case VirtualKey.Up when !onButton:
                MoveSelection(-1);
                break;
            case VirtualKey.Down when !onButton:
                MoveSelection(1);
                break;
            case VirtualKey.Enter when !onButton && _editing >= 0:
                TextEditor.Focus(FocusState.Keyboard);
                TextEditor.SelectionStart = TextEditor.Text.Length;
                break;
            case VirtualKey.Delete when !onButton && !_busy:
                DeleteSelected();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>The text box holds typing that is not an edit yet: Ctrl+Z belongs to the box then.</summary>
    private bool HasTypedChange(object? box)
    {
        if (_editor is null || _editing < 0 || _editing >= Count)
            return false;
        var cue = _editor.Cues[_editing];
        return ReferenceEquals(box, TextEditor) ? TextEditor.Text != cue.Text
            : ReferenceEquals(box, StartBox) ? StartBox.Text != CueTime.Format(cue.Start, 3)
            : ReferenceEquals(box, EndBox) && EndBox.Text != CueTime.Format(cue.End, 3);
    }

    private void OnPlay(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnStepBack(object sender, RoutedEventArgs e) => Seek(Position - StepSeconds);

    private void OnStepForward(object sender, RoutedEventArgs e) => Seek(Position + StepSeconds);

    private void OnVideoTapped(object sender, TappedRoutedEventArgs e) => TogglePlay();

    private void OnZoomIn(object sender, RoutedEventArgs e) => Timeline.Zoom(0.5);

    private void OnZoomOut(object sender, RoutedEventArgs e) => Timeline.Zoom(2);

    private async void OnBack(object sender, RoutedEventArgs e) => await CloseAsync();
}
