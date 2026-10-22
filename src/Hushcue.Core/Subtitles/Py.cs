using System.Globalization;
using System.Numerics;
using System.Text;

namespace Hushcue.Core.Subtitles;

/// <summary>
/// The few Python string semantics the clean-up depends on, so the port produces byte-identical subtitles:
/// what counts as whitespace (<c>str.isspace</c> / regex <c>\s</c>), <c>len</c> in code points,
/// <c>round</c> to even (also to a number of digits), and <c>format(x, ".0f")</c>.
/// </summary>
internal static class Py
{
    public static bool IsSpace(char c) =>
        c is (>= (char)0x09 and <= (char)0x0D) or (>= (char)0x1C and <= (char)0x20) or (char)0x85 or (char)0xA0
          or (char)0x1680 or (>= (char)0x2000 and <= (char)0x200A) or (char)0x2028 or (char)0x2029 or (char)0x202F
          or (char)0x205F or (char)0x3000;

    public static string Strip(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start]))
            start++;
        while (end > start && IsSpace(s[end - 1]))
            end--;
        return s[start..end];
    }

    public static string RStrip(string s)
    {
        var end = s.Length;
        while (end > 0 && IsSpace(s[end - 1]))
            end--;
        return s[..end];
    }

    /// <summary><c>str.split()</c>: runs of whitespace separate, no empty parts.</summary>
    public static string[] Split(string s)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && IsSpace(s[i]))
                i++;
            var start = i;
            while (i < s.Length && !IsSpace(s[i]))
                i++;
            if (i > start)
                parts.Add(s[start..i]);
        }
        return [.. parts];
    }

    /// <summary><c>re.sub(r"\s+", " ", s)</c>.</summary>
    public static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var inSpace = false;
        foreach (var c in s)
        {
            if (IsSpace(c))
            {
                if (!inSpace)
                    sb.Append(' ');
                inSpace = true;
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }
        return sb.ToString();
    }

    /// <summary><c>len(s)</c>: code points, not UTF-16 units.</summary>
    public static int Len(string s)
    {
        var n = s.Length;
        for (var i = 0; i + 1 < s.Length; i++)
        {
            if (char.IsSurrogatePair(s[i], s[i + 1]))
            {
                n--;
                i++;
            }
        }
        return n;
    }

    /// <summary><c>int(round(x))</c>: halves go to the even neighbour.</summary>
    public static long Round(double x) => (long)Math.Round(x, MidpointRounding.ToEven);

    /// <summary>
    /// <c>round(x, digits)</c>: CPython rounds the exact binary value of <paramref name="x"/> (halves to even), where
    /// <see cref="Math.Round(double, int, MidpointRounding)"/> rounds an inexact <c>x * 10^digits</c>.
    /// </summary>
    public static double Round(double x, int digits)
    {
        if (!double.IsFinite(x) || x == 0)
            return x;
        // x = mantissa * 2^exponent exactly
        var bits = BitConverter.DoubleToInt64Bits(x);
        var biased = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
        if (biased == 0)
            biased = 1;
        else
            mantissa |= 1L << 52;
        var exponent = biased - 1075;

        var numerator = mantissa * BigInteger.Pow(10, digits);
        var denominator = BigInteger.One;
        if (exponent > 0)
            numerator <<= exponent;
        else
            denominator <<= -exponent;
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !quotient.IsEven))
            quotient++;
        var rounded = (double)quotient / Math.Pow(10, digits);
        return bits < 0 ? -rounded : rounded;
    }

    /// <summary><c>f"{x:.0f}"</c>.</summary>
    public static string F0(double x) => Math.Round(x, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture);
}
