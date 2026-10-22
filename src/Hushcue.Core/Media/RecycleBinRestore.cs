using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace Hushcue.Core.Media;

/// <summary>
/// Undo for <see cref="RecycleBin.Send"/>: puts a file back where it was, as the Recycle Bin's own Restore does.
/// Each recycled file is a pair in <c>X:\$Recycle.Bin\&lt;user SID&gt;</c>: <c>$R…</c> holds the data and <c>$I…</c>
/// records where it came from and when it was deleted.
/// </summary>
public static class RecycleBinRestore
{
    /// <summary>Whether <paramref name="originalPath"/> is in the current user's Recycle Bin.</summary>
    public static bool Contains(string originalPath) => OperatingSystem.IsWindows() && Find(Path.GetFullPath(originalPath)) is not null;

    /// <summary>Move the most recently recycled copy of <paramref name="originalPath"/> back into place.</summary>
    /// <returns>false when it is no longer in the Recycle Bin or a file with that name exists again</returns>
    public static bool Restore(string originalPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var path = Path.GetFullPath(originalPath);
        if (File.Exists(path) || Find(path) is not { } item)
            return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(item.Data, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        try
        {
            File.Delete(item.Info); // the record of the deleted file; without its data it would be a dead entry
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the file is back either way
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static (string Info, string Data)? Find(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        var root = Path.GetPathRoot(path);
        if (sid is null || string.IsNullOrEmpty(root))
            return null;
        var bin = Path.Combine(root, "$Recycle.Bin", sid);
        (string Info, string Data)? newest = null;
        var newestTime = DateTime.MinValue;
        try
        {
            foreach (var info in Directory.EnumerateFiles(bin, "$I*"))
            {
                if (ReadInfo(File.ReadAllBytes(info)) is not { } record
                    || !string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase) || record.Deleted < newestTime)
                    continue;
                var data = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
                if (File.Exists(data))
                    (newest, newestTime) = ((info, data), record.Deleted);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return newest;
    }

    /// <summary>
    /// Reads a <c>$I</c> record: version (8 bytes), size (8), deletion time as a FILETIME (8), then the original path in
    /// UTF-16: 260 characters in version 1 (Windows Vista to 8.1), a 4-byte length and the path in version 2 (Windows 10+).
    /// </summary>
    internal static (string Path, DateTime Deleted)? ReadInfo(byte[] bytes)
    {
        if (bytes.Length < 24)
            return null;
        var version = BitConverter.ToInt64(bytes, 0);
        var deleted = DateTime.FromFileTimeUtc(Math.Max(BitConverter.ToInt64(bytes, 16), 0));
        string text;
        if (version == 2 && bytes.Length >= 28)
        {
            var chars = BitConverter.ToInt32(bytes, 24);
            if (chars <= 0 || 28 + chars * 2L > bytes.Length)
                return null;
            text = Encoding.Unicode.GetString(bytes, 28, chars * 2);
        }
        else if (version == 1)
        {
            text = Encoding.Unicode.GetString(bytes, 24, Math.Min(bytes.Length - 24, 520));
        }
        else
        {
            return null;
        }
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text, deleted);
    }
}
