using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Hushcue.Services;

/// <summary>
/// One Hushcue per user session. The first instance holds the mutex "Hushcue" (the installer's AppMutex, so setup can
/// tell the app is running) and listens on a named pipe; a later launch, such as one per file when several files are
/// opened from Explorer, sends its files down the pipe and exits, and the first instance queues them and comes to the
/// front.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = "Hushcue";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex, bool isFirst) => (_mutex, IsFirst) = (mutex, isFirst);

    public bool IsFirst { get; }

    // per user and session: another user's Hushcue on the same PC is not this one
    private static string PipeName => $"Hushcue-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";

    public static SingleInstance Claim()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    /// <summary>A later launch: hand the files to the running Hushcue and let it take the foreground.</summary>
    /// <returns>false if the running instance could not be reached</returns>
    public static bool Forward(IEnumerable<string> paths)
    {
        AllowSetForegroundWindow(AnyProcess);
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(ConnectTimeout);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var path in paths)
                writer.WriteLine(path);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The first instance: receive later launches' files until the app ends.</summary>
    /// <param name="onLaunch">called on a pool thread with each later launch's files (possibly none)</param>
    public void Listen(Action<List<string>> onLaunch)
    {
        if (!IsFirst)
            throw new InvalidOperationException("Only the first instance listens.");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                                                       PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var paths = new List<string>();
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (line.Length > 0)
                            paths.Add(line);
                    }
                    onLaunch(paths);
                }
                catch (IOException)
                {
                    // a launch that went away mid-way: wait for the next
                }
            }
        });
    }

    public void Dispose()
    {
        if (IsFirst)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    /// <summary>Restore a minimised window and bring it to the front (the launch that forwarded allowed it).</summary>
    public static void BringToFront(IntPtr window)
    {
        if (IsIconic(window))
            ShowWindow(window, Restore);
        SetForegroundWindow(window);
    }

    private const int AnyProcess = -1, Restore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
