using Hushcue.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;

namespace Hushcue;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();
        TitleText.Text = Product.Name;
        TaglineText.Text = Product.Tagline;
        AppWindow.Resize(new SizeInt32(1040, 740));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hushcue.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 640;
        }

        Generate.Load(_settings);
        Convert.Load(_settings);
        Modes.SelectedItem = _settings.Page == 1 ? ConvertItem : GenerateItem;
        AppWindow.Closing += OnClosing;
    }

    private Controls.FileQueue ActiveQueue => Convert.Visibility == Visibility.Visible ? Convert.Files : Generate.Files;

    /// <summary>Queue files on the page that is showing.</summary>
    public void AddFiles(IEnumerable<string> paths) => ActiveQueue.AddPaths(paths);

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var convert = sender.SelectedItem == ConvertItem;
        Generate.Visibility = convert ? Visibility.Collapsed : Visibility.Visible;
        Convert.Visibility = convert ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
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
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        queue.AddPaths(items.OfType<StorageFile>().Select(f => f.Path).Where(p => p.Length > 0));
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        Generate.Save(_settings);
        Convert.Save(_settings);
        _settings.Page = Modes.SelectedItem == ConvertItem ? 1 : 0;
        _settings.Save();
        Generate.Stop();
        Convert.Stop();
        Core.Pipeline.SubtitleJob.Unload();
    }
}
