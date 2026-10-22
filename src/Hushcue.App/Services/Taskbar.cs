using System.Runtime.InteropServices;

namespace Hushcue.Services;

/// <summary>The window's taskbar button: a progress bar while a batch runs, and a flash when it ends unseen.</summary>
public sealed class Taskbar(IntPtr window)
{
    public enum State
    {
        None = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    private ITaskbarList3? _list;

    /// <param name="fraction">0..1, or null for a moving bar while nothing can be measured yet</param>
    public void SetProgress(double? fraction, State state = State.Normal)
    {
        if (List() is not { } list)
            return;
        if (fraction is { } value && state is not (State.None or State.Indeterminate))
        {
            list.SetProgressState(window, state);
            list.SetProgressValue(window, (ulong)(Math.Clamp(value, 0, 1) * 1000), 1000);
        }
        else
        {
            list.SetProgressState(window, fraction is null && state != State.None ? State.Indeterminate : state);
        }
    }

    public void Clear() => SetProgress(null, State.None);

    /// <summary>Flash the taskbar button until the window comes to the front, if it is not in front already.</summary>
    public void FlashIfInBackground()
    {
        if (GetForegroundWindow() == window)
            return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = window,
            Flags = FlashTray | FlashUntilForeground,
            Count = 3,
        };
        FlashWindowEx(ref info);
    }

    public static bool IsInForeground(IntPtr window) => GetForegroundWindow() == window;

    private ITaskbarList3? List()
    {
        if (_list is not null)
            return _list;
        try
        {
            var list = (ITaskbarList3)new TaskbarList();
            list.HrInit();
            return _list = list;
        }
        catch (COMException)
        {
            return null; // no taskbar (e.g. some server editions): progress is a nicety
        }
    }

    private const uint FlashTray = 0x2, FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr window);
        void DeleteTab(IntPtr window);
        void ActivateTab(IntPtr window);
        void SetActiveAlt(IntPtr window);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3 (only the first two are used)
        void SetProgressValue(IntPtr window, ulong completed, ulong total);
        void SetProgressState(IntPtr window, State state);
    }
}
