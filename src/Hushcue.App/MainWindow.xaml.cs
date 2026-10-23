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
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI;

namespace Hushcue;

/// <summary>The pages the sidebar switches between; the first three are remembered between runs.</summary>
public enum AppPage
{
    Generate,
    Convert,
    Edit,
    Settings,
    About,
}

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private AppPage _page;
    private readonly Taskbar _taskbar;
    private readonly Updater _updater;
    private string? _gpuName;
    private bool _closeConfirmed;
    private double _scale;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Standard height: the caption buttons are exactly as tall as the title bar. Tall ones (48 px) hung below it,
        // so their hover boxes (and Close's red one) stuck out into the page.
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hushcue.ico"));
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowPlacement.Open(AppWindow, handle);
        _taskbar = new Taskbar(handle);
        _updater = new Updater(_settings, PrepareToExitAsync);

        Generate.Load(_settings);
        Convert.Load(_settings);
        Edit.Load(_settings);
        SettingsView.Load(_settings);
        Edit.IsJobRunning = () => Generate.IsRunning;
        Generate.SettingsRequested += (_, _) => Navigate(AppPage.Settings);
        SettingsView.Changed += (_, _) =>
        {
            Generate.RefreshSettingsSummary();
            ApplyTheme();
        };
        SettingsView.SystemCheckRequested += async (_, _) => await RunSystemCheckAsync();
        About.UpdateCheckRequested += async (_, _) => await _updater.CheckNowAsync(Root.XamlRoot);
        Root.Loaded += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4)); // let the window settle; never in the way of the setup screen
            if (Setup.Visibility != Visibility.Visible)
                await _updater.CheckOnStartAsync(Root.XamlRoot);
        };
        ApplyTheme();
        Navigate(_settings.Page is >= 0 and <= (int)AppPage.Edit ? (AppPage)_settings.Page : AppPage.Generate);
        foreach (var page in new IRunPage[] { Generate, Convert })
        {
            page.ProgressChanged += (_, progress) => _taskbar.SetProgress(progress.Fraction, progress.State);
            page.Finished += (_, _) =>
            {
                _taskbar.Clear();
                _taskbar.FlashIfInBackground();
            };
        }
        Generate.DeviceChanged += (_, device) => ShowStatus(device);
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
        Root.Loaded += (_, _) =>
        {
            _scale = Root.XamlRoot.RasterizationScale;
            Root.XamlRoot.Changed += OnXamlRootChanged;
        };
        Root.Loaded += async (_, _) =>
        {
            if (await AppIcon.LoadAsync() is { } icon)
                AppTitleBar.IconSource = new ImageIconSource { ImageSource = icon }; // instead of the glyph
        };

        ShowSetup(SetupView.IsNeeded(), firstRun: true);
        _ = Setup.CheckAsync();
    }

    /// <summary>The queue files go to: the MKV page's on that page, else the Generate page's.</summary>
    private Controls.FileQueue ActiveQueue => _page == AppPage.Convert ? Convert.Files : Generate.Files;

    /// <summary>Show a page and select it in the sidebar.</summary>
    private void Navigate(AppPage page)
    {
        _page = page;
        Nav.SelectedItem = page switch
        {
            AppPage.Convert => ConvertItem,
            AppPage.Edit => EditItem,
            AppPage.Settings => Nav.SettingsItem,
            AppPage.About => AboutItem,
            _ => GenerateItem,
        };
        ShowPage();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var item = args.SelectedItem as NavigationViewItem;
        _page = args.IsSettingsSelected ? AppPage.Settings
            : item == ConvertItem ? AppPage.Convert
            : item == EditItem ? AppPage.Edit
            : item == AboutItem ? AppPage.About
            : AppPage.Generate;
        ShowPage();
    }

    /// <summary>The current page, or the setup screen over everything while it is open.</summary>
    private void ShowPage()
    {
        var setup = Setup.Visibility == Visibility.Visible;
        Generate.Visibility = Shown(!setup && _page == AppPage.Generate);
        Convert.Visibility = Shown(!setup && _page == AppPage.Convert);
        Edit.Visibility = Shown(!setup && _page == AppPage.Edit);
        SettingsView.Visibility = Shown(!setup && _page == AppPage.Settings);
        About.Visibility = Shown(!setup && _page == AppPage.About);
    }

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The window moved to a monitor with another scale: its minimum size follows, in effective pixels.</summary>
    private void OnXamlRootChanged(XamlRoot root, XamlRootChangedEventArgs args)
    {
        if (root.RasterizationScale == _scale)
            return;
        _scale = root.RasterizationScale;
        WindowPlacement.SetMinimum(AppWindow, _scale);
    }

    private void ApplyTheme() => Root.RequestedTheme = _settings.Theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };


    /// <summary>
    /// The minimize / maximize / close buttons are drawn by Windows in its own theme: match the app's, which can differ
    /// (Settings or HUSHCUE_THEME). Windows only lets their colours change, not their shape, and a hover background is
    /// a hard square against the rounded, flat title bar, so the background always stays see-through and hovering
    /// brightens the symbol instead. (Close keeps Windows' own red hover.)
    /// </summary>
    private void ColorCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        bar.ButtonForegroundColor = dark ? Color.FromArgb(0xFF, 0xC8, 0xC8, 0xC8) : Color.FromArgb(0xFF, 0x40, 0x40, 0x40);
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonPressedForegroundColor = dark ? Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0) : Color.FromArgb(0xFF, 0x60, 0x60, 0x60);
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A) : Color.FromArgb(0xFF, 0x90, 0x90, 0x90);
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = bar.ButtonPressedBackgroundColor = Colors.Transparent;
    }

    private readonly List<SubtitlePair> _pendingEdits = [];
    private readonly List<string> _pendingOrphans = [];
    private string? _pendingVideo;

    /// <summary>
    /// Queue files on the page that is showing. Subtitles (.srt) open in the editor with their video instead: dropped
    /// together, passed on the command line, or chosen from Explorer's right-click menu.
    /// </summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var (pairs, orphans, others) = SubtitlePairs.Split(paths);
        if (_page == AppPage.Edit && pairs.Count == 0 && others.FirstOrDefault(SubtitlePairs.IsMedia) is { } video)
        {
            // on the editor's page a video on its own opens with the subtitles next to it
            _pendingVideo = video;
            others = [.. others.Where(p => p != video)];
        }
        if (others.Count > 0)
        {
            if (_page is not (AppPage.Generate or AppPage.Convert))
                Navigate(AppPage.Generate);
            ActiveQueue.AddPaths(others);
        }
        if (pairs.Count == 0 && orphans.Count == 0 && _pendingVideo is null)
            return;
        _pendingEdits.AddRange(pairs);
        _pendingOrphans.AddRange(orphans);
        OpenPendingEdits();
    }

    /// <summary>Open what was asked for once the window is up and past the setup screen (editing needs no model).</summary>
    private async void OpenPendingEdits()
    {
        if (Root.XamlRoot is null || Setup.Visibility == Visibility.Visible
            || (_pendingEdits.Count == 0 && _pendingOrphans.Count == 0 && _pendingVideo is null))
            return;
        var pairs = _pendingEdits.ToList();
        var orphans = _pendingOrphans.ToList();
        var video = _pendingVideo;
        _pendingEdits.Clear();
        _pendingOrphans.Clear();
        _pendingVideo = null;
        Navigate(AppPage.Edit);
        foreach (var pair in pairs.Skip(1))
            Edit.Remember(pair); // one opens; the others wait in Recent
        if (pairs.Count > 0)
            await Edit.OpenAsync(pairs[0].Video, pairs[0].Srt);
        else if (video is not null)
            await Edit.OpenAsync(video, null);
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
        Nav.IsPaneVisible = !show;
        ShowPage();
    }

    private void OnGpuChecked(GpuStatus gpu)
    {
        _gpuName = gpu.Name;
        var device = gpu.Cuda ? "cuda" : "cpu";
        Generate.SetDevice(device);
        ShowStatus(device);
        SettingsView.SetDevice(gpu.Cuda && gpu.Name is not null ? $"On your {gpu.Name}."
            : gpu.Name is not null ? $"On the CPU: {gpu.Name} is there, but CUDA or its libraries are not ready. Run the system check."
            : "On the CPU: no NVIDIA graphics card was found. It works, more slowly.");
        if (Setup.Visibility != Visibility.Visible && !Setup.IsReadyToRun)
            ShowSetup(true, firstRun: true); // the engine itself is missing
    }

    /// <summary>
    /// The sidebar's status entry: "Offline · RTX 4070" while the GPU does the work, "Offline · CPU" without one or
    /// after a GPU failure. The icon is green on the GPU; the text stays like the other entries.
    /// </summary>
    private void ShowStatus(string device)
    {
        var gpu = device == "cuda" && _gpuName is not null;
        var text = gpu ? $"Offline · {SystemCheck.ShortGpuName(_gpuName!)}" : "Offline · CPU";
        var where = gpu ? $"on your {_gpuName}" : "on the CPU";
        var tip = $"Everything runs on this PC; nothing is uploaded. Transcribing {where}. Select for a system check.";
        StatusItem.Content = text;
        ToolTipService.SetToolTip(StatusItem, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusItem, $"{text}. {tip}");
        StatusIcon.Foreground = ThemeBrushes.Get(gpu ? ThemeBrushes.Success : ThemeBrushes.Secondary);
        StatusItem.Visibility = Visibility.Visible;
    }

    private async void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer == StatusItem)
            await RunSystemCheckAsync();
    }

    private async Task RunSystemCheckAsync()
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
        if (e.Key != VirtualKey.Enter || !ctrl || Setup.Visibility == Visibility.Visible
            || _page is not (AppPage.Generate or AppPage.Convert))
            return;
        (_page == AppPage.Convert ? (IRunPage)Convert : Generate).Start();
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || Setup.Visibility == Visibility.Visible)
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = _page == AppPage.Edit ? "Open in the editor" : "Add to the queue";
        if (_page is AppPage.Generate or AppPage.Convert)
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
        if (!_closeConfirmed && (Generate.HasUnsavedEdits || Edit.HasUnsavedEdits))
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

    /// <summary>
    /// Before an update's installer takes over: ask about unsaved edits and a running batch like closing does, then stop
    /// everything. False when the user wants to keep working.
    /// </summary>
    private async Task<bool> PrepareToExitAsync()
    {
        if (Generate.HasUnsavedEdits || Edit.HasUnsavedEdits)
        {
            if (!await Dialogs.ConfirmAsync(Root.XamlRoot, "Your edits to the subtitles are not saved. Update without saving them?",
                                            "Update without saving", "Keep editing"))
                return false;
        }
        if (Generate.IsRunning || Convert.IsRunning)
        {
            if (!await Dialogs.ConfirmAsync(Root.XamlRoot, "Files are still being processed. Stop them and update?\n\n"
                                                           + "Finished files are kept; the file in progress is not saved.",
                                            "Stop and update", "Keep working"))
                return false;
        }
        _closeConfirmed = true;
        ShutDown();
        return true;
    }

    /// <summary>Remember the choices and stop everything that is running.</summary>
    private void ShutDown()
    {
        Generate.Save(_settings);
        Convert.Save(_settings);
        _settings.Page = _page <= AppPage.Edit ? (int)_page : 0;
        _settings.Save();
        Setup.Stop();
        Edit.Stop();
        Generate.Stop();
        Convert.Stop();
        _taskbar.Clear();
        Core.Pipeline.SubtitleJob.Unload();
    }
}
