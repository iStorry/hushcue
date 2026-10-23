using System.Collections.ObjectModel;
using System.Diagnostics;
using Hushcue.Controls;
using Hushcue.Core;
using Hushcue.Core.Engine;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Hushcue.Core.Review;
using Hushcue.Core.Subtitles;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    /// <summary>The subtitle over the video, as a share of the picture's height (as players draw it), within these sizes.</summary>
    private const double OverlayTextShare = 0.055;
    private const double MinOverlayFontSize = 12;
    private const double MaxOverlayFontSize = 40;
    private const double WidescreenRatio = 16.0 / 9;
    /// <summary>A playhead this far outside the comparison being played was moved there, so it stops showing it.</summary>
    private const double SeekSlackSeconds = 0.5;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly MediaPlayer _player = new() { AutoPlay = false };
    private ObservableCollection<ReviewRow> _rows = [];
    private QueuedFile? _file;
    private Transcript? _transcript;
    private CueEditor? _editor;
    private MediaSource? _media;
    private float[]? _audio;
    private CancellationTokenSource? _reading;
    /// <summary>Embedding, or trying another model: what Cancel stops.</summary>
    private CancellationTokenSource? _work;
    private Comparison? _comparison;
    /// <summary>Playing the comparison stops here, and shows its subtitles over the video until then.</summary>
    private double? _playUntil;
    private bool _comparisonPlayed;
    private string? _srtPath;
    private string? _output;
    private string? _recycled;
    private int _editing = -1;
    private int _current = -1;
    private bool _syncing;
    private bool _busy;
    private AppSettings _settings = new();

    /// <summary>What another quality heard in [Start, End), waiting to be used or not.</summary>
    private sealed record Comparison(double Start, double End, Preset Preset, List<Cue> Cues);

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

    /// <summary>The app's settings: the player's volume is kept there, so it is the same next time.</summary>
    public void UseSettings(AppSettings settings)
    {
        _settings = settings;
        SetVolume(settings.Volume, settings.Muted);
    }

    /// <param name="volume">0..1</param>
    private void SetVolume(double volume, bool muted)
    {
        volume = Math.Clamp(volume, 0, 1);
        (_settings.Volume, _settings.Muted) = (volume, muted);
        _player.Volume = volume;
        _player.IsMuted = muted;
        var percent = (int)Math.Round(volume * 100);
        if ((int)VolumeSlider.Value != percent)
            VolumeSlider.Value = percent;
        VolumeText.Text = $"{percent}%";
        VolumeIcon.Glyph = muted || percent == 0 ? "" // mute
            : percent < 34 ? "" : percent < 67 ? "" : "";
        MuteIcon.Glyph = muted ? "" : "";
        var name = muted ? "Volume, muted" : $"Volume {percent}%";
        ToolTipService.SetToolTip(VolumeButton, $"{name} (M {(muted ? "unmutes" : "mutes")})");
        AutomationProperties.SetName(VolumeButton, name);
        ToolTipService.SetToolTip(MuteButton, muted ? "Unmute (M)" : "Mute (M)");
        AutomationProperties.SetName(MuteButton, muted ? "Unmute" : "Mute");
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if ((int)Math.Round(_settings.Volume * 100) != (int)e.NewValue)
            SetVolume(e.NewValue / 100, muted: false); // moving the slider unmutes, as in most players
    }

    private void OnMute(object sender, RoutedEventArgs e) => SetVolume(_settings.Volume, !_settings.Muted);

    /// <summary>The subtitle over the video grows and shrinks with the picture, which fills the area's width or height.</summary>
    private void OnVideoAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var picture = Math.Min(e.NewSize.Height, e.NewSize.Width / WidescreenRatio);
        OverlayText.FontSize = Math.Clamp(picture * OverlayTextShare, MinOverlayFontSize, MaxOverlayFontSize);
    }

    public async Task OpenAsync(QueuedFile file)
    {
        var transcript = file.Transcript ?? throw new InvalidOperationException($"{file.Name} has no subtitles to review yet.");
        (_file, _transcript, _output, _recycled, _audio) = (file, transcript, null, null, null);
        DropComparison();
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
        var unsure = transcript.Unsure ?? (transcript.Srt is { } saved ? UnsureWords.Load(saved) : []);
        _editor = new CueEditor(cues, Math.Max(transcript.Duration, file.Duration), unsure);
        _editor.Changed += OnEdited;

        FileTitle.Text = file.Name;
        ModeBox.SelectedIndex = transcript.Settings.Embed == EmbedMode.Burn ? 1 : 0;
        ResultLinks.Visibility = Visibility.Collapsed;
        WorkBar.Visibility = Visibility.Collapsed;
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
        _work?.Cancel();
        DropComparison();
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
            var enhance = PartTranscription.Enhances(settings); // as the run heard it, for re-runs
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
        if (_playUntil is { } until)
        {
            // Play() takes a moment to start, so a pause only counts once it has played
            _comparisonPlayed |= playing;
            var paused = _comparisonPlayed && session.PlaybackState == MediaPlaybackState.Paused;
            if (_comparison is null || paused || position >= until || position < _comparison.Start - SeekSlackSeconds)
            {
                _playUntil = null;
                if (playing && position >= until && position < until + SeekSlackSeconds) // got there by playing, not a seek
                    _player.Pause();
            }
        }
        var index = _editor.IndexAt(position);
        // while the comparison plays, the other model's subtitles are drawn in their own colour instead
        var text = index >= 0 ? _editor.Cues[index].Text : null;
        var other = _playUntil is not null && _comparison is { } c && position >= c.Start && position < c.End;
        if (other)
            text = _comparison!.Cues.FirstOrDefault(q => position >= q.Start && position < q.End)?.Text;
        OverlayText.Text = text is null ? "" : Cleanup.WrapLines(text);
        OverlayText.Foreground = other ? OtherModelBrush : WhiteBrush;
        Overlay.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
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
        ShowSwaps();
    }

    /// <summary>At most this many words of a subtitle get swap buttons, the unsure ones first.</summary>
    private const int MaxSwapWords = 3;

    /// <summary>
    /// One-click swaps for the selected subtitle's look-alike words ("calm → come · cum"): always for words Whisper
    /// was not sure of, and for the less common look-alikes ("principle", not "come" or "to") even when it was.
    /// </summary>
    private void ShowSwaps()
    {
        SwapPanel.Children.Clear();
        var words = new List<(TextWord Word, bool Unsure)>();
        if (_editor is not null && _editing >= 0)
        {
            var cue = _editor.Cues[_editing];
            var unsure = UnsureWords.Occurrences(cue, _editor.Unsure).Select(o => o.Word).ToList();
            words = [.. LookAlikes.Words(cue.Text)
                                  .Select(w => (Word: w, Unsure: unsure.Contains(w)))
                                  .Where(w => LookAlikes.IsLookAlike(w.Word.Text) && (w.Unsure || !LookAlikes.IsCommon(w.Word.Text)))
                                  .OrderByDescending(w => w.Unsure)
                                  .Take(MaxSwapWords)
                                  .OrderBy(w => w.Word.Index)];
        }
        foreach (var (word, unsure) in words)
        {
            var line = new WrapPanel { HorizontalSpacing = 4, VerticalSpacing = 4 };
            line.Children.Add(new TextBlock
            {
                Text = $"{word.Text} →",
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ThemeBrushes.Get(unsure ? ThemeBrushes.Caution : ThemeBrushes.Secondary),
            });
            foreach (var other in LookAlikes.Others(word.Text))
            {
                var button = new Button { Content = other, Style = (Style)Resources["ToolButton"] };
                var what = $"Change “{word.Text}” to “{other}”";
                ToolTipService.SetToolTip(button, what);
                AutomationProperties.SetName(button, what);
                button.Click += (_, _) => Swap(word, other);
                line.Children.Add(button);
            }
            SwapPanel.Children.Add(line);
        }
        SwapPanel.Visibility = words.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Swap(TextWord word, string replacement)
    {
        if (_editor is null || _editing < 0)
            return;
        var text = _editor.Cues[_editing].Text;
        // the text may have changed since the buttons were made (typed, then committed when the button took focus)
        if (word.Index + word.Length > text.Length || text.Substring(word.Index, word.Length) != word.Text)
        {
            LoadEditor();
            return;
        }
        _editor.SetText(_editing, LookAlikes.Replace(text, word, replacement));
        StatusText.Text = $"Changed “{word.Text}” to “{replacement}” (undo with Ctrl+Z)";
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
        if (_comparison is not null)
            DropComparison(); // made against the subtitles as they were
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

    /// <summary>
    /// The stretch "Re-run this part" would transcribe: the selected gap, or the selected subtitles and a little of the
    /// silence either side (<see cref="PartTranscription.Widen"/>).
    /// </summary>
    private (double Start, double End)? RerunRange()
    {
        if (_editor is null)
            return null;
        if (SelectedRow is { IsGap: true } gap && CueList.SelectedItems.Count == 1)
            return (gap.Start, gap.End);
        if (SelectedCues is not [var first, ..] cues)
            return null;
        var last = cues[^1];
        var before = first > 0 ? _editor.Cues[first - 1].End : 0;
        var after = last + 1 < Count ? _editor.Cues[last + 1].Start : _editor.Duration;
        return PartTranscription.Widen(_editor.Cues[first].Start, _editor.Cues[last].End, before, after);
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
            var cues = await PartTranscription.RunAsync(PartAudio(), start, end, _transcript.Settings, _ => { });
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

    private PartAudio PartAudio() => new(_file!.Path, _audio!, PartTranscription.Enhances(_transcript!.Settings));

    // --- try another model

    private static readonly SolidColorBrush WhiteBrush = new(Colors.White);
    /// <summary>The other model's subtitles over the video: a soft yellow, so they are not mistaken for the real ones.</summary>
    private static readonly SolidColorBrush OtherModelBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xE0, 0x8A));

    /// <summary>One item per quality: its model, and the download when that model is not on this PC.</summary>
    private void OnCompareMenuOpening(object? sender, object e)
    {
        CompareMenu.Items.Clear();
        var own = Presets.Get(_transcript?.Settings.Preset ?? Presets.Default).Name;
        foreach (var preset in Presets.All)
        {
            var text = $"{preset.Name} · {preset.Model}" + (preset.Name == own ? " (as Re-run this part)" : "")
                       + (PartTranscription.HasModel(preset.Name) ? "" : $" · download {Gigabytes(ModelSize(preset))}");
            var item = new MenuFlyoutItem { Text = text };
            ToolTipService.SetToolTip(item, preset.Description);
            item.Click += (_, _) => CompareWith(preset);
            CompareMenu.Items.Add(item);
        }
    }

    private static long ModelSize(Preset preset) => Models.Sources.TryGetValue(preset.Model, out var source) ? source.Size : 0;

    private static string Gigabytes(long bytes) => $"{bytes / 1e9:0.0} GB";

    /// <summary>
    /// Transcribe the selected stretch with <paramref name="preset"/>'s model (downloading it first, if asked) and
    /// show what it heard next to what is there now, to use or not.
    /// </summary>
    private async void CompareWith(Preset preset)
    {
        if (_editor is null || _transcript is null || _audio is null || RerunRange() is not var (start, end))
            return;
        if (IsJobRunning())
        {
            StatusText.Text = "Trying another model needs the speech engine, which the files being transcribed are using. Try again when they are done.";
            return;
        }
        CommitPending();
        DropComparison();
        var range = CueTime.Range(start, end);
        if (!PartTranscription.HasModel(preset.Name)
            && !await Dialogs.ConfirmAsync(XamlRoot,
                   $"{preset.Name} uses the {preset.Model} model, which is not on this PC yet. Download it ({Gigabytes(ModelSize(preset))})? "
                   + "It stays for next time, and a stopped download carries on where it left off.", "Download"))
            return;
        var work = _work = new CancellationTokenSource();
        SetBusy(true, cancellable: true);
        var ui = DispatcherQueue;
        try
        {
            if (!PartTranscription.HasModel(preset.Name))
            {
                StatusText.Text = $"Downloading {preset.Model}...";
                await Task.Run(() => ModelDownload.DownloadAsync(preset.Model, (done, total) => ui.TryEnqueue(() =>
                {
                    WorkBar.Value = total > 0 ? done * 1000.0 / total : 0;
                    StatusText.Text = $"Downloading {preset.Model}: {Gigabytes(done)} of {Gigabytes(total)}";
                }), work.Token), work.Token);
            }
            var editor = _editor;
            WorkBar.IsIndeterminate = true;
            StatusText.Text = $"Transcribing {range} with {preset.Name} ({preset.Model})...";
            var cues = await PartTranscription.RunAsync(PartAudio(), start, end, _transcript.Settings with { Preset = preset.Name },
                                                        message => ui.TryEnqueue(() => StatusText.Text = message), work.Token);
            if (editor != _editor)
                return; // the page moved on
            if (cues.Count == 0)
            {
                StatusText.Text = $"{preset.Name} heard nothing in {range}; the subtitles are unchanged.";
                return;
            }
            ShowComparison(new Comparison(start, end, preset, cues));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stopped; the subtitles are unchanged";
        }
        catch (Exception ex) when (ex is EngineException or MediaException or ModelDownloadException or InvalidOperationException
                                       or IOException or HttpRequestException)
        {
            StatusText.Text = $"Could not try {preset.Name} here: " + ex.Message;
        }
        finally
        {
            if (_work == work)
                _work = null;
            WorkBar.IsIndeterminate = false;
            SetBusy(false);
        }
    }

    /// <summary>The cues <see cref="CueEditor.ReplaceRange"/> would replace for [start, end).</summary>
    private List<Cue> CuesIn(double start, double end) =>
        _editor is null ? [] : [.. _editor.Cues.SkipWhile(c => c.Start < start).TakeWhile(c => c.Start < end)];

    private void ShowComparison(Comparison comparison)
    {
        _comparison = comparison;
        var range = CueTime.Range(comparison.Start, comparison.End);
        var name = comparison.Preset.Name;
        var before = string.Join(" ", CuesIn(comparison.Start, comparison.End).Select(c => c.Text));
        var after = string.Join(" ", comparison.Cues.Select(c => c.Text));
        var (left, right) = TextDiff.Words(before, after);
        CompareTitle.Text = $"{name.ToUpperInvariant()} · {comparison.Preset.Model.ToUpperInvariant()} HEARD {range}";
        CompareAfterLabel.Text = name;
        ShowDiff(CompareBefore, left, removed: true);
        ShowDiff(CompareAfter, right, removed: false);
        if (left.Count == 0)
            CompareBefore.Text = "(no subtitles)";
        AutomationProperties.SetName(CompareAfter, $"What {name} heard");
        var changes = left.Count(w => w.Changed) + right.Count(w => w.Changed);
        var count = comparison.Cues.Count;
        CompareSummary.Text = (changes == 0 ? "The same words as now" : changes == 1 ? "1 word differs" : $"{changes} words differ")
                              + $" · {count} subtitle{(count == 1 ? "" : "s")}";
        UseCompareText.Text = $"Use {name}'s";
        ToolTipService.SetToolTip(UseCompareButton, $"Replace the subtitles in {range} with these (Ctrl+Z puts them back)");
        ToolTipService.SetToolTip(PlayCompareButton, $"Play {range} with {name}'s subtitles over the video, in yellow");
        AutomationProperties.SetName(UseCompareButton, $"Use {name}'s subtitles for {range}");
        AutomationProperties.SetName(PlayCompareButton, $"Play {range} with {name}'s subtitles");
        ComparePanel.Visibility = Visibility.Visible;
        StatusText.Text = $"{name} heard {range}: compare, then use it or keep the current subtitles";
        Timeline.Reveal(comparison.Start, comparison.End);
        UseCompareButton.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The words into <paramref name="block"/>, the changed ones marked: struck through when <paramref name="removed"/>,
    /// otherwise bold in the accent colour.
    /// </summary>
    private static void ShowDiff(TextBlock block, List<DiffWord> words, bool removed)
    {
        block.Inlines.Clear();
        for (var i = 0; i < words.Count; i++)
        {
            var run = new Run { Text = (i > 0 ? " " : "") + words[i].Text };
            if (words[i].Changed && removed)
            {
                run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                run.Foreground = ThemeBrushes.Get(ThemeBrushes.Secondary);
            }
            else if (words[i].Changed)
            {
                run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                run.Foreground = ThemeBrushes.Get(ThemeBrushes.Accent);
            }
            block.Inlines.Add(run);
        }
    }

    private void DropComparison()
    {
        _comparison = null;
        _playUntil = null;
        ComparePanel.Visibility = Visibility.Collapsed;
    }

    private void OnUseComparison(object sender, RoutedEventArgs e)
    {
        if (_editor is null || _comparison is not { } comparison)
            return;
        DropComparison();
        var range = CueTime.Range(comparison.Start, comparison.End);
        _editor.ReplaceRange(comparison.Start, comparison.End, comparison.Cues, $"Use {comparison.Preset.Name} for {range}");
        StatusText.Text = $"Used {comparison.Preset.Name}'s subtitles for {range}. Ctrl+Z puts the old ones back.";
        FocusSelectedRow();
    }

    private void OnKeepCurrent(object sender, RoutedEventArgs e)
    {
        if (_comparison is not { } comparison)
            return;
        DropComparison();
        StatusText.Text = $"Kept the current subtitles for {CueTime.Range(comparison.Start, comparison.End)}";
        FocusSelectedRow();
    }

    private void OnPlayComparison(object sender, RoutedEventArgs e)
    {
        if (_comparison is not { } comparison || _player.Source is null)
            return;
        Seek(comparison.Start);
        _playUntil = comparison.End;
        _comparisonPlayed = false;
        _player.Play();
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
        if (_srtPath != path)
            UnsureWords.Save(path, _editor.Unsure);
        _srtPath = path;
        _editor.MarkSaved();
        _transcript = _transcript with { Cues = [.. _editor.Cues], Srt = path, Unsure = _editor.Unsure };
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
        var embedding = _work = new CancellationTokenSource();
        SetBusy(true, cancellable: true);
        StatusText.Text = mode == EmbedMode.Soft ? "Adding the subtitle track..." : "Burning the subtitles into the video...";
        if (recycle)
            ReleaseMedia(); // the preview holds the file open
        var ui = DispatcherQueue;
        try
        {
            var result = await Task.Run(() => SubtitleJob.EmbedAsync(file.Path, cues, _transcript.OutDir, mode, recycle,
                f => ui.TryEnqueue(() => WorkBar.Value = f * 1000), _ => { }, embedding.Token));
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
            if (_work == embedding)
                _work = null;
            SetBusy(false);
        }
    }

    private void OnCancelWork(object sender, RoutedEventArgs e) => _work?.Cancel();

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

    /// <param name="cancellable">embedding or trying another model: the progress bar and Cancel show</param>
    private void SetBusy(bool busy, bool cancellable = false)
    {
        _busy = busy;
        WorkBar.Value = 0;
        WorkBar.Visibility = busy && cancellable ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy && cancellable ? Visibility.Visible : Visibility.Collapsed;
        EmbedButton.Visibility = busy && cancellable ? Visibility.Collapsed : Visibility.Visible;
        if (busy)
            ResultLinks.Visibility = Visibility.Collapsed;
        UpdateCommands();
        if (busy && cancellable)
            CancelButton.Focus(FocusState.Programmatic);
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
        RerunButton.IsEnabled = CompareButton.IsEnabled = idle && _audio is not null && RerunRange() is not null;
        UseCompareButton.IsEnabled = KeepCompareButton.IsEnabled = PlayCompareButton.IsEnabled = idle;
        ToolTipService.SetToolTip(RerunButton, _audio is null
            ? "Available once the audio has been read"
            : "Transcribe the selected stretch again and replace its subtitles (Ctrl+Z puts them back)");
        ToolTipService.SetToolTip(CompareButton, _audio is null
            ? "Available once the audio has been read"
            : "Transcribe the selected stretch with another quality's model and compare before using it");
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
            case VirtualKey.M:
                SetVolume(_settings.Volume, !_settings.Muted);
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
