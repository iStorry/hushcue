namespace Hushcue.Core.Review;

/// <summary>A word of a text being compared, and whether the other text has it.</summary>
public sealed record DiffWord(string Text, bool Changed);

/// <summary>
/// Which words differ between two versions of a stretch's subtitles, to show what another model heard next to what is
/// there now. Words are matched by their longest common sequence, ignoring case and the punctuation around them, so a
/// moved comma or a capital at a new sentence start does not count as a different word.
/// </summary>
public static class TextDiff
{
    /// <summary>The words of <paramref name="before"/> and of <paramref name="after"/>, each marked when the other text lacks it.</summary>
    public static (List<DiffWord> Before, List<DiffWord> After) Words(string before, string after)
    {
        var a = Split(before);
        var b = Split(after);
        var ka = a.Select(Key).ToArray();
        var kb = b.Select(Key).ToArray();
        // common[i, j]: the longest common sequence of a[i..] and b[j..]
        var common = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                common[i, j] = ka[i] == kb[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
        var left = new List<DiffWord>();
        var right = new List<DiffWord>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && ka[x] == kb[y])
            {
                left.Add(new DiffWord(a[x++], false));
                right.Add(new DiffWord(b[y++], false));
            }
            else if (y < b.Length && (x == a.Length || common[x, y + 1] >= common[x + 1, y]))
                right.Add(new DiffWord(b[y++], true));
            else
                left.Add(new DiffWord(a[x++], true));
        }
        return (left, right);
    }

    /// <summary>How many words of <paramref name="after"/> are not in <paramref name="before"/>, plus those dropped.</summary>
    public static int Changes(string before, string after)
    {
        var (left, right) = Words(before, after);
        return left.Count(w => w.Changed) + right.Count(w => w.Changed);
    }

    private static string[] Split(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string Key(string word)
    {
        var trimmed = word.Trim([.. word.Where(c => !char.IsLetterOrDigit(c) && c != '\'' && c != '’')]);
        return (trimmed.Length > 0 ? trimmed : word).ToLowerInvariant().Replace('’', '\'');
    }
}
