using System.Runtime.InteropServices;

namespace Hushcue.Core.Media;

public static class RecycleBin
{
    /// <summary>
    /// Move a file to the Windows Recycle Bin (recoverable), silently; false if that was not possible.
    /// Never deletes outright.
    /// </summary>
    public static bool Send(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
            return false;
        var op = new FileOperation
        {
            Function = FoDelete,
            From = Path.GetFullPath(path) + "\0", // the list ends with a double null; the marshaller adds the second
            Flags = FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi,
        };
        return SHFileOperationW(ref op) == 0 && !op.Aborted && !File.Exists(path);
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref FileOperation op);
}
