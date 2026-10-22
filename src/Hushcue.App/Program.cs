using Hushcue.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hushcue;

/// <summary>The entry point (instead of the one XAML generates): a second launch forwards its files and exits.</summary>
public static class Program
{
    internal static SingleInstance? Instance { get; private set; }

    /// <summary>The files on a command line ("Open with Hushcue", Explorer's menu): existing paths only, made absolute.</summary>
    internal static List<string> Files(IEnumerable<string> args) => [.. args.Where(File.Exists).Select(Path.GetFullPath)];

    [STAThread]
    private static int Main(string[] args)
    {
        using var instance = SingleInstance.Claim();
        // HUSHCUE_NEW_INSTANCE=1 starts a separate window even when Hushcue is open (for testing builds side by side)
        if (!instance.IsFirst && Environment.GetEnvironmentVariable("HUSHCUE_NEW_INSTANCE") != "1"
            && SingleInstance.Forward(Files(args)))
            return 0;
        Instance = instance;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
