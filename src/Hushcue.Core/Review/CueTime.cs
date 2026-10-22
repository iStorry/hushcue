using System.Globalization;

namespace Hushcue.Core.Review;

/// <summary>Times as the review screen shows and accepts them: "04:12.3", "1:04:12.345".</summary>
public static class CueTime
{
    /// <summary>"04:12.3" (minutes and seconds, <paramref name="decimals"/> digits of the second); "1:04:12.3" from an hour.</summary>
    public static string Format(double seconds, int decimals = 1)
    {
        var scale = Math.Pow(10, decimals);
        var total = Math.Round(Math.Max(seconds, 0) * scale, MidpointRounding.AwayFromZero) / scale; // 59.96 shows as 01:00.0
        var hours = (int)(total / 3600);
        var minutes = (int)(total % 3600 / 60);
        var rest = total % 60;
        var secondsText = rest.ToString(decimals > 0 ? "00." + new string('0', decimals) : "00", CultureInfo.InvariantCulture);
        return hours > 0 ? $"{hours}:{minutes:00}:{secondsText}" : $"{minutes:00}:{secondsText}";
    }

    /// <summary>"04:19–04:51", whole seconds: a stretch of the file.</summary>
    public static string Range(double start, double end) => $"{Format(start, 0)}–{Format(end, 0)}";

    /// <summary>A length for people: "32 s", "2 min 5 s".</summary>
    public static string Length(double seconds) =>
        seconds < 60 ? $"{Math.Round(seconds):F0} s" : $"{(int)(seconds / 60)} min {Math.Round(seconds % 60):F0} s";

    /// <summary>
    /// Read a time typed by the user: "4:12.3", "04:12,345", "1:04:12", or plain seconds ("252.3").
    /// </summary>
    public static bool TryParse(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length is 0 or > 3 || parts.Any(p => p.Length == 0))
            return false;
        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var secs))
            return false;
        var whole = 0;
        foreach (var part in parts[..^1])
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return false;
            whole = whole * 60 + value;
        }
        if (parts.Length > 1 && secs >= 60)
            return false;
        seconds = whole * 60 + secs;
        return true;
    }
}
