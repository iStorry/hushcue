using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Hushcue.Services;

/// <summary>
/// The main window's size, in effective pixels scaled to its monitor (AppWindow works in physical pixels, so fixed
/// numbers were half the size on a 200% screen). It opens at <see cref="Preferred"/>, centred, and can't be made
/// smaller than <see cref="Minimum"/>; both shrink to the monitor's work area on a small screen.
/// </summary>
internal static class WindowPlacement
{
    private const double Dpi100Percent = 96;
    private static readonly (int Width, int Height) Preferred = (1040, 740);

    /// <summary>Small enough to sit beside another window on a laptop screen; the pages adapt down to it.</summary>
    private static readonly (int Width, int Height) Minimum = (600, 500);

    /// <summary>Size and centre the window on the monitor it opens on, and set its minimum size.</summary>
    public static void Open(AppWindow window, IntPtr handle)
    {
        var scale = GetDpiForWindow(handle) / Dpi100Percent;
        var area = WorkArea(window);
        var width = Math.Min(Scaled(Preferred.Width, scale), area.Width);
        var height = Math.Min(Scaled(Preferred.Height, scale), area.Height);
        window.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        SetMinimum(window, scale);
    }

    /// <summary>The minimum size for the window's monitor: again after it moved to a screen with another scale.</summary>
    public static void SetMinimum(AppWindow window, double scale)
    {
        if (window.Presenter is not OverlappedPresenter presenter)
            return;
        var area = WorkArea(window);
        presenter.PreferredMinimumWidth = Math.Min(Scaled(Minimum.Width, scale), area.Width);
        presenter.PreferredMinimumHeight = Math.Min(Scaled(Minimum.Height, scale), area.Height);
    }

    private static RectInt32 WorkArea(AppWindow window) =>
        DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).WorkArea;

    private static int Scaled(int effectivePixels, double scale) => (int)Math.Round(effectivePixels * scale);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
