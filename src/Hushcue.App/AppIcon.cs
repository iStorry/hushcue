using Microsoft.UI.Xaml.Media.Imaging;

namespace Hushcue;

/// <summary>The app icon as a vector image (Assets\Hushcue.svg), sharp at any size, for the title bar and About.</summary>
internal static class AppIcon
{
    private static string SvgPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Hushcue.svg");

    /// <summary>The icon, or null if the file is missing (then callers keep their fallback).</summary>
    public static async Task<SvgImageSource?> LoadAsync()
    {
        if (!File.Exists(SvgPath))
            return null;
        var svg = new SvgImageSource();
        using var stream = File.OpenRead(SvgPath);
        var status = await svg.SetSourceAsync(stream.AsRandomAccessStream());
        return status == SvgImageSourceLoadStatus.Success ? svg : null;
    }
}
