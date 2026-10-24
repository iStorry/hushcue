using Hushcue.Controls;
using Hushcue.Core;
using Hushcue.Core.Media;
using Hushcue.Core.Subtitles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Hushcue.Pages;

/// <summary>A recently edited video and its subtitles, as the start page lists it.</summary>
public sealed class RecentEdit(SubtitlePair pair)
{
    public SubtitlePair Pair { get; } = pair;
    public string Name => Path.GetFileName(Pair.Video);
    public string Detail => $"{Path.GetFileName(Pair.Srt)} · {Path.GetDirectoryName(Pair.Video)}";
}

/// <summary>
/// "Edit subtitles": open a video with subtitles it already has in the editor (the review page), without transcribing.
/// </summary>
public sealed partial class EditPage : UserControl
{
    private AppSettings _settings = new();
    private bool _opening;

    public EditPage()
    {
        InitializeComponent();
        Review.Closed += (_, _) => ShowEditor(false);
    }

    /// <summary>Whether a batch is using the engine (the editor's "Re-run this part" waits for it).</summary>
    public Func<bool> IsJobRunning
    {
        set => Review.IsJobRunning = value;
    }

    public bool IsEditing => Review.Visibility == Visibility.Visible;

    public bool HasUnsavedEdits => IsEditing && Review.HasUnsavedEdits;

    public void Load(AppSettings settings)
    {
        _settings = settings;
        Review.UseSettings(settings);
        RefreshRecent();
    }

    public void Stop() => Review.Shutdown();

    /// <summary>Keep a pair in the recent list without opening it (more than one dropped at once).</summary>
    public void Remember(SubtitlePair pair)
    {
        _settings.AddRecentEdit(pair);
        RefreshRecent();
    }

    /// <summary>
    /// Open <paramref name="video"/> with <paramref name="srt"/>, else the subtitles next to it ("name.srt" or
    /// "name.en.srt"), else a subtitle track inside it saved as an .srt next to it, else ones the user picks.
    /// </summary>
    public async Task OpenAsync(string video, string? srt)
    {
        if (_opening || (IsEditing && !await Review.CloseAsync()))
            return;
        _opening = true;
        try
        {
            srt ??= SubtitlePairs.FindSubtitles(video) ?? await FromTrackAsync(video);
        }
        finally
        {
            _opening = false;
            ShowBusy(null);
        }
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

        var file = new QueuedFile(video);
        try
        {
            file.SetProbe(await Task.Run(() => MediaProbe.ReadAsync(video)), null);
        }
        catch (Exception e) when (e is MediaException or IOException or System.Text.Json.JsonException
                                    or System.ComponentModel.Win32Exception)
        {
            await Dialogs.ErrorAsync(XamlRoot, $"Could not open {file.Name}: {e.Message}");
            return;
        }
        file.Update(Core.FileProgress.Done(video, srt));
        file.SetTranscript(new Transcript(cues, srt, _settings.ToJob(), _settings.OutputFolder.Length > 0 ? _settings.OutputFolder : null,
                                          Math.Max(file.Duration, cues[^1].End)));
        Remember(new SubtitlePair(video, srt));
        ShowEditor(true);
        await Review.OpenAsync(file);
    }

    private void ShowEditor(bool show)
    {
        Review.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Landing.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show)
            RefreshRecent();
    }

    private void RefreshRecent()
    {
        var recent = _settings.RecentEdits.Where(p => File.Exists(p.Video) && File.Exists(p.Srt)).Select(p => new RecentEdit(p)).ToList();
        RecentList.ItemsSource = recent;
        RecentPanel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRecentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentEdit recent)
            await OpenAsync(recent.Pair.Video, recent.Pair.Srt);
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        _settings.RecentEdits.Clear();
        _settings.Save(); // gone now, not only when the app closes
        RefreshRecent();
    }

    private async void OnOpenVideo(object sender, RoutedEventArgs e)
    {
        if (await PickAsync(MediaFiles.Extensions) is { } video)
            await OpenAsync(video, null);
    }

    private async void OnOpenSubtitles(object sender, RoutedEventArgs e)
    {
        if (await PickAsync([".srt"]) is not { } srt)
            return;
        var video = SubtitlePairs.FindVideo(srt);
        if (video is null)
        {
            if (!await Dialogs.ConfirmAsync(XamlRoot, $"There is no video next to {Path.GetFileName(srt)} with the same name. "
                                                      + "Pick the video it belongs to?", "Pick the video"))
                return;
            video = await PickAsync(MediaFiles.Extensions);
        }
        if (video is not null)
            await OpenAsync(video, srt);
    }

    /// <summary>
    /// The video's own subtitle track saved as an .srt next to it: the only text track, or the one the user picks.
    /// With no text track (none at all, or only pictures of text) the user can pick an .srt instead.
    /// </summary>
    private async Task<string?> FromTrackAsync(string video)
    {
        var name = Path.GetFileName(video);
        List<SubtitleTrack> tracks;
        try
        {
            tracks = await Task.Run(() => SubtitleTracks.ReadAsync(video));
        }
        catch (Exception e) when (e is MediaException or IOException or System.Text.Json.JsonException
                                    or System.ComponentModel.Win32Exception)
        {
            tracks = []; // an unreadable file is reported when it is opened
        }
        var text = tracks.Where(t => t.IsText).ToList();
        var pictures = tracks.Count - text.Count;
        if (text.Count == 0)
        {
            var formats = string.Join(", ", tracks.Select(t => t.Format).Distinct());
            return await PickSubtitlesAsync(video, pictures == 0
                ? $"There is no .srt next to {name}, and no subtitle track inside it."
                : $"The subtitles inside {name} are pictures of text ({formats}), not text, so they can't be edited here. "
                  + "Turning them into text needs an OCR tool, such as Subtitle Edit.");
        }
        var track = text[0];
        if (text.Count > 1)
        {
            var message = $"{name} has {text.Count} subtitle tracks. Which one do you want to edit? It is saved as an .srt next to the video.";
            if (pictures > 0)
                message += $" ({pictures} more {(pictures == 1 ? "is" : "are")} pictures of text, which can't be edited here.)";
            var index = await Dialogs.ChooseAsync(XamlRoot, message, text.Select(t => t.Label).ToList(), "Edit this track");
            if (index < 0)
                return null;
            track = text[index];
        }
        var srt = SubtitleTracks.SrtPath(video, track);
        ShowBusy($"Reading the subtitle track from {name}...");
        try
        {
            await Task.Run(() => SubtitleTracks.ExtractAsync(video, track, srt));
        }
        catch (Exception e) when (e is MediaException or IOException or UnauthorizedAccessException
                                    or System.ComponentModel.Win32Exception)
        {
            ShowBusy(null);
            await Dialogs.ErrorAsync(XamlRoot, $"Could not save the subtitle track of {name} as {Path.GetFileName(srt)}: {e.Message}");
            return null;
        }
        return srt;
    }

    private void ShowBusy(string? message)
    {
        BusyText.Text = message ?? "";
        BusyPanel.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        OpenButtons.Visibility = message is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<string?> PickSubtitlesAsync(string video, string reason)
    {
        if (!await Dialogs.ConfirmAsync(XamlRoot, $"{reason} Pick the subtitles to edit?", "Pick an .srt"))
            return null;
        return await PickAsync([".srt"]);
    }

    private static async Task<string?> PickAsync(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in extensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        return (await picker.PickSingleFileAsync())?.Path;
    }
}
