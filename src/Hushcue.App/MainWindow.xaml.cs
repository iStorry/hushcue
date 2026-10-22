using Hushcue.Controls;
using Hushcue.Core;
using Hushcue.Core.Subtitles;
using Hushcue.Pages;
using Hushcue.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI;

namespace Hushcue;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Taskbar _taskbar;
    private string? _gpuName;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new SizeInt32(1040, 740));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hushcue.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 640;
        }
        _taskbar = new Taskbar(WinRT.Interop.WindowNative.GetWindowHandle(this));

        Generate.Load(_settings);
        Convert.Load(_settings);
        Modes.SelectedItem = _settings.Page == 1 ? ConvertItem : GenerateItem;
        foreach (var page in new IRunPage[] { Generate, Convert })
        {
            page.ProgressChanged += (_, progress) => _taskbar.SetProgress(progress.Fraction, progress.State);
            page.Finished += (_, _) =>
            {
                _taskbar.Clear();
                _taskbar.FlashIfInBackground();
            };
        }
        Generate.DeviceChanged += (_, device) => ShowBadge(device);
        Setup.GpuChecked += (_, gpu) => OnGpuChecked(gpu);
        Setup.Continued += (_, _) =>
        {
            ShowSetup(false);
            OpenPendingEdits();
        };
        Root.Loaded += (_, _) => OpenPendingEdits();
        AppWindow.Closing += OnClosing;
        Root.ActualThemeChanged += (_, _) => ColorCaptionButtons();
        Root.Loaded += (_, _) => ColorCaptionButtons();

        ShowSetup(SetupView.IsNeeded(), firstRun: true);
        _ = Setup.CheckAsync();
    }

    private Controls.FileQueue ActiveQueue => Convert.Visibility == Visibility.Visible ? Convert.Files : Generate.Files;


    /// <summary>
    /// The minimize / maximize / close buttons are drawn by Windows in its own theme: match the app's, which can differ
    /// (HUSHCUE_THEME), and keep their background see-through over the title bar.
    /// </summary>
    private void ColorCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var text = dark ? Colors.White : Colors.Black;
        bar.ButtonForegroundColor = bar.ButtonHoverForegroundColor = bar.ButtonPressedForegroundColor = text;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00);
    }

    private readonly List<SubtitlePair> _pendingEdits = [];
    private readonly List<string> _pendingOrphans = [];

    /// <summary>
    /// Queue files on the page that is showing. Subtitles (.srt) open in the editor with their video instead: dropped
    /// together, passed on the command line, or chosen from Explorer's right-click menu.
    /// </summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var (pairs, orphans, others) = SubtitlePairs.Split(paths);
        if (others.Count > 0)
            ActiveQueue.AddPaths(others);
        if (pairs.Count == 0 && orphans.Count == 0)
            return;
        _pendingEdits.AddRange(pairs);
        _pendingOrphans.AddRange(orphans);
        OpenPendingEdits();
    }

    /// <summary>Open what was asked for once the window is up and past the setup screen (editing needs no model).</summary>
    private async void OpenPendingEdits()
    {
        if (Root.XamlRoot is null || Setup.Visibility == Visibility.Visible || (_pendingEdits.Count == 0 && _pendingOrphans.Count == 0))
            return;
        var pairs = _pendingEdits.ToList();
        var orphans = _pendingOrphans.ToList();
        _pendingEdits.Clear();
        _pendingOrphans.Clear();
        Modes.SelectedItem = GenerateItem;
        for (var i = 0; i < pairs.Count; i++)
            await Generate.OpenInEditorAsync(pairs[i].Video, pairs[i].Srt, open: i == 0);
        if (orphans.Count > 0)
        {
            var names = string.Join(", ", orphans.Select(Path.GetFileName));
            await Dialogs.InfoAsync(Root.XamlRoot, $"No video was found next to {names}. Keep the video in the same folder "
                                                   + "with the same name (e.g. talk.mp4 and talk.srt), or use Edit subtitles.");
        }
    }

    private void ShowSetup(bool show, bool firstRun = false)
    {
        Setup.SetFirstRun(firstRun);
        Setup.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Modes.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ShowPage(show ? null : Modes.SelectedItem == ConvertItem);
    }

    /// <param name="convert">null hides both pages</param>
    private void ShowPage(bool? convert)
    {
        Generate.Visibility = convert == false ? Visibility.Visible : Visibility.Collapsed;
        Convert.Visibility = convert == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Setup.Visibility != Visibility.Visible)
            ShowPage(sender.SelectedItem == ConvertItem);
    }

    private void OnGpuChecked(GpuStatus gpu)
    {
        _gpuName = gpu.Name;
        var device = gpu.Cuda ? "cuda" : "cpu";
        Generate.SetDevice(device);
        ShowBadge(device);
        if (Setup.Visibility != Visibility.Visible && !Setup.IsReadyToRun)
            ShowSetup(true, firstRun: true); // the engine itself is missing
    }

    /// <summary>"Offline · RTX 4070" while the GPU does the work, "Offline · CPU" without one or after a GPU failure.</summary>
    private void ShowBadge(string device)
    {
        var gpu = device == "cuda" && _gpuName is not null;
        BadgeText.Text = gpu ? $"Offline · {SystemCheck.ShortGpuName(_gpuName!)}" : "Offline · CPU";
        var where = gpu ? $"on your {_gpuName}" : "on the CPU";
        var tip = $"Everything runs on this PC; nothing is uploaded. Transcribing {where}. Select for a system check.";
        ToolTipService.SetToolTip(Badge, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Badge, $"{BadgeText.Text}. {tip}");
        var (fill, text) = gpu
            ? ("SystemFillColorSuccessBackgroundBrush", ThemeBrushes.Success)
            : ("SystemFillColorNeutralBackgroundBrush", ThemeBrushes.Secondary);
        Badge.Background = ThemeBrushes.Get(fill);
        BadgeText.Foreground = BadgeIcon.Foreground = ThemeBrushes.Get(text);
        Badge.Visibility = Visibility.Visible;
    }

    private async void OnBadgeClick(object sender, RoutedEventArgs e)
    {
        if (Setup.Visibility == Visibility.Visible || Generate.IsRunning || Convert.IsRunning)
            return;
        ShowSetup(true);
        await Setup.CheckAsync();
    }

    /// <summary>
    /// Ctrl+Enter starts from anywhere, also from the file list and text boxes, which take plain Enter for themselves
    /// (as a keyboard accelerator it never reached the start button from there).
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key != VirtualKey.Enter || !ctrl || Setup.Visibility == Visibility.Visible)
            return;
        (Convert.Visibility == Visibility.Visible ? (IRunPage)Convert : Generate).Start();
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || Setup.Visibility == Visibility.Visible)
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to the queue";
        ActiveQueue.SetDropActive(true);
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ActiveQueue.SetDropActive(false);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var queue = ActiveQueue;
        queue.SetDropActive(false);
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || Setup.Visibility == Visibility.Visible)
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        AddFiles(items.OfType<StorageFile>().Select(f => f.Path).Where(p => p.Length > 0));
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_closeConfirmed && Generate.HasUnsavedEdits)
        {
            args.Cancel = true;
            if (!await Dialogs.ConfirmAsync(Root.XamlRoot, "Your edits to the subtitles are not saved. Close Hushcue without saving them?",
                                            "Close without saving", "Keep editing"))
                return;
            _closeConfirmed = !(Generate.IsRunning || Convert.IsRunning); // a running batch still asks below
            if (_closeConfirmed)
            {
                ShutDown();
                Close();
                return;
            }
        }
        if (!_closeConfirmed && (Generate.IsRunning || Convert.IsRunning))
        {
            args.Cancel = true;
            if (!await Dialogs.ConfirmAsync(Root.XamlRoot, "Files are still being processed. Stop and close Hushcue?\n\n"
                                                           + "Finished files are kept; the file in progress is not saved.",
                                            "Stop and close", "Keep working"))
                return;
            _closeConfirmed = true;
            ShutDown(); // Close() doesn't raise Closing again, and the job must stop before its ffmpeg outlives the app
            Close();
            return;
        }
        ShutDown();
    }

    /// <summary>Remember the choices and stop everything that is running.</summary>
    private void ShutDown()
    {
        Generate.Save(_settings);
        Convert.Save(_settings);
        _settings.Page = Modes.SelectedItem == ConvertItem ? 1 : 0;
        _settings.Save();
        Setup.Stop();
        Generate.Stop();
        Convert.Stop();
        _taskbar.Clear();
        Core.Pipeline.SubtitleJob.Unload();
    }
}
