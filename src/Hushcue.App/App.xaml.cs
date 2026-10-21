using Microsoft.UI.Xaml;

namespace Hushcue;

public partial class App : Application
{
    private static MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>The main window's handle, for file pickers (desktop apps must give them an owner window).</summary>
    public static IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(_window);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        // Files passed on the command line ("Open with Hushcue") go straight into the queue.
        _window.AddFiles(Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).Select(Path.GetFullPath));
        _window.Activate();
    }
}
