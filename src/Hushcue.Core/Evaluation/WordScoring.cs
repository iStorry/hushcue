using System.Text;

namespace Hushcue.Core.Evaluation;

/// <summary>A word of a reference transcript; <paramref name="Scored"/> words are also counted on their own.</summary>
public sealed record ReferenceWord(string Text, bool Scored);

/// <summary>A scored word the transcript got wrong: what it wrote instead (null: nothing) and about where.</summary>
public sealed record WordMiss(string Expected, string? Got, double? At);

/// <param name="Words">words in the reference</param>
/// <param name="Errors">substitutions, deletions and insertions against it</param>
/// <param name="Scored">scored reference words</param>
/// <param name="ScoredRight">scored words the transcript has, spelled the same</param>
/// <param name="Aligned">for each reference word, the transcript word lined up with it (null: none)</param>
public sealed record WordScore(int Words, int Errors, int Scored, int ScoredRight, IReadOnlyList<WordMiss> Misses,
                               IReadOnlyList<int?> Aligned)
{
    public double ErrorRate => Words == 0 ? 0 : (double)Errors / Words;
}

/// <summary>
/// Word error rate and per-word accuracy of a transcript against a reference, for <c>Hushcue.Cli eval</c>. Words are
/// compared in lower case without punctuation, but apostrophes count: "its" and "it's" are different words.
/// </summary>
public static class WordScoring
{
    /// <summary>The words of a reference where words in *stars* are scored ("stay *calm*").</summary>
    public static List<ReferenceWord> Reference(string marked) =>
        [.. marked.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                  .SelectMany(token => Words(token.Replace("*", "")).Select(w => new ReferenceWord(w, token.Contains('*'))))];

    /// <summary>Lower case words without punctuation; hyphens and dashes split words, apostrophes stay inside them.</summary>
    public static List<string> Words(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            plain.Append(char.IsLetterOrDigit(c) ? c : c is '\'' or '’' or '‘' ? '\'' : ' ');
        return [.. plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => w.Trim('\''))
                        .Where(w => w.Length > 0)];
    }

    /// <summary>Align <paramref name="transcript"/> (words with the time they were said) to the reference.</summary>
    public static WordScore Compare(IReadOnlyList<ReferenceWord> reference, IReadOnlyList<(string Word, double At)> transcript)
    {
        int n = reference.Count, m = transcript.Count;
        var cost = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++)
            cost[i, 0] = i;
        for (var j = 0; j <= m; j++)
            cost[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var same = reference[i - 1].Text == transcript[j - 1].Word ? 0 : 1;
                cost[i, j] = Math.Min(cost[i - 1, j - 1] + same, Math.Min(cost[i - 1, j], cost[i, j - 1]) + 1);
            }

        // walk back: for each reference word, the transcript word it lines up with (or none)
        var matched = new int?[n];
        for (int i = n, j = m; i > 0;)
        {
            if (j > 0 && cost[i, j] == cost[i - 1, j - 1] + (reference[i - 1].Text == transcript[j - 1].Word ? 0 : 1))
                matched[--i] = --j;
            else if (j > 0 && cost[i, j] == cost[i, j - 1] + 1)
                j--;
            else
                i--;
        }

        var misses = new List<WordMiss>();
        var scored = 0;
        for (var i = 0; i < n; i++)
        {
            if (!reference[i].Scored)
                continue;
            scored++;
            if (matched[i] is { } j && transcript[j].Word == reference[i].Text)
                continue;
            misses.Add(new WordMiss(reference[i].Text, matched[i] is { } k ? transcript[k].Word : null, NearestTime(matched, transcript, i)));
        }
        return new WordScore(n, cost[n, m], scored, scored - misses.Count, misses, matched);
    }

    private static double? NearestTime(int?[] matched, IReadOnlyList<(string Word, double At)> transcript, int i)
    {
        for (var back = i; back >= 0; back--)
            if (matched[back] is { } j)
                return transcript[j].At;
        return transcript.Count > 0 ? transcript[0].At : null;
    }
}
