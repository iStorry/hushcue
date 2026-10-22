using Microsoft.UI.Xaml;

namespace Hushcue;

public partial class App : Application
{
    private static MainWindow? _window;

    public App()
    {
        // The app follows the Windows theme; HUSHCUE_THEME=Light or Dark pins one (for screenshots and testing).
        if (Enum.TryParse<ApplicationTheme>(Environment.GetEnvironmentVariable("HUSHCUE_THEME"), ignoreCase: true, out var theme))
            RequestedTheme = theme;
        InitializeComponent();
    }

    /// <summary>The main window's handle, for file pickers (desktop apps must give them an owner window).</summary>
    public static IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(_window);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = _window = new MainWindow();
        // Files passed on the command line ("Open with Hushcue") go straight into the queue
        window.AddFiles(Program.Files(Environment.GetCommandLineArgs().Skip(1)));
        window.Activate();
        // ... and so do those of later launches, which hand them over and exit
        if (Program.Instance is { IsFirst: true } instance)
        {
            instance.Listen(paths => window.DispatcherQueue.TryEnqueue(() =>
            {
                window.AddFiles(paths);
                Services.SingleInstance.BringToFront(WindowHandle);
            }));
        }
    }
}
