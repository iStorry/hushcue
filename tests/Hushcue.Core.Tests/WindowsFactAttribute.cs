namespace Hushcue.Core.Tests;

/// <summary>A test of something only Windows has (the Recycle Bin); skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
    }
}
