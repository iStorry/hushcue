using Hushcue.Controls;
using Hushcue.Core;
using Hushcue.Pages;
using Hushcue.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
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
        Setup.Continued += (_, _) => ShowSetup(false);
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

    /// <summary>Queue files on the page that is showing.</summary>
    public void AddFiles(IEnumerable<string> paths) => ActiveQueue.AddPaths(paths);

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
        queue.AddPaths(items.OfType<StorageFile>().Select(f => f.Path).Where(p => p.Length > 0));
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
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
