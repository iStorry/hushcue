using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hushcue;

internal static class ThemeBrushes
{
    public const string Primary = "TextFillColorPrimaryBrush";
    public const string Secondary = "TextFillColorSecondaryBrush";
    public const string Success = "SystemFillColorSuccessBrush";
    public const string Critical = "SystemFillColorCriticalBrush";
    public const string Caution = "SystemFillColorCautionBrush";
    public const string Accent = "AccentFillColorDefaultBrush";
    public const string StrongStroke = "ControlStrongStrokeColorDefaultBrush";

    /// <summary>A brush from the app's theme resources, by key.</summary>
    public static Brush Get(string key) =>
        Application.Current.Resources[key] is Brush brush
            ? brush
            : throw new KeyNotFoundException($"'{key}' is not a brush in the app's theme resources.");
}
