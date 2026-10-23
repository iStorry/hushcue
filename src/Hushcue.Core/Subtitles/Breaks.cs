namespace Hushcue.Core.Subtitles;

/// <summary>
/// How good a place between two words is for a line or subtitle break, by Netflix's English line-treatment rules:
/// break after punctuation, before a conjunction or a preposition; never split an article from its noun, a subject
/// pronoun or auxiliary from its verb, a preposition from its object, a first name from a last name, or a number from
/// what it counts. Lower is better.
/// </summary>
public static class Breaks
{
    public const int SentenceEnd = 0;
    public const int ClauseEnd = 1;
    public const int BeforeConjunction = 2;
    public const int BeforePreposition = 3;
    public const int Neutral = 5;
    public const int Splits = 9;

    private static readonly HashSet<string> Conjunctions =
    [
        "and", "but", "or", "nor", "so", "yet", "because", "although", "though", "while", "whereas", "when", "whenever",
        "where", "wherever", "if", "unless", "until", "since", "that", "which", "who", "whom", "whose", "than", "then",
    ];

    private static readonly HashSet<string> Prepositions =
    [
        "about", "above", "across", "after", "against", "along", "among", "around", "at", "before", "behind", "below",
        "beneath", "beside", "between", "beyond", "by", "during", "except", "for", "from", "in", "inside", "into", "like",
        "near", "of", "off", "on", "onto", "out", "outside", "over", "past", "through", "throughout", "to", "toward",
        "towards", "under", "upon", "with", "within", "without",
    ];

    /// <summary>Words that belong with the word after them: articles, possessives, subject pronouns, auxiliaries and
    /// modals (with their contractions), negations; prepositions are added from <see cref="Prepositions"/>.</summary>
    private static readonly HashSet<string> BindsForward =
    [
        "a", "an", "the", "my", "your", "his", "her", "its", "our", "their", "this", "these", "those", "every", "each",
        "no", "i", "he", "she", "we", "they", "am", "is", "are", "was", "were", "be", "been", "being", "have", "has",
        "had", "do", "does", "did", "will", "would", "shall", "should", "can", "could", "may", "might", "must", "not",
        "don't", "doesn't", "didn't", "won't", "wouldn't", "can't", "couldn't", "shouldn't", "isn't", "aren't", "wasn't",
        "weren't", "haven't", "hasn't", "hadn't", "i'm", "you're", "we're", "they're", "he's", "she's", "it's", "i've",
        "you've", "we've", "they've", "i'll", "you'll", "we'll", "they'll", "he'll", "she'll", "i'd", "you'd", "we'd",
        "they'd", "he'd", "she'd", "very", "too", "so-called",
    ];

    private static readonly char[] Closers = ['"', '\'', ')', ']', '”', '’', '»'];

    /// <summary>The cost of breaking between <paramref name="before"/> and <paramref name="after"/> (words as
    /// Whisper writes them: leading space, punctuation attached).</summary>
    public static int Cost(string before, string after)
    {
        var left = Py.Strip(before).TrimEnd(Closers);
        if (left.Length == 0)
            return Neutral;
        if (left[^1] is '.' or '?' or '!' or '…')
            return SentenceEnd;
        if (left[^1] is ',' or ';' or ':' or '-' or '—')
            return ClauseEnd;
        var leftKey = Key(left);
        var rightKey = Key(after);
        if (Conjunctions.Contains(rightKey) && !BindsForward.Contains(leftKey))
            return BeforeConjunction;
        if (BindsForward.Contains(leftKey) || Prepositions.Contains(leftKey) || char.IsDigit(left[^1]) || IsNamePair(left, after))
            return Splits;
        return Prepositions.Contains(rightKey) ? BeforePreposition : Neutral;
    }

    /// <summary>Two capitalised words in a row ("Maria Garcia"), where the first is not "I".</summary>
    private static bool IsNamePair(string left, string after)
    {
        var right = Py.Strip(after);
        return left.Length > 1 && char.IsUpper(left[0]) && right.Length > 0 && char.IsUpper(right[0]);
    }

    private static string Key(string word) =>
        Py.Strip(word).Trim('"', '\'', '(', '[', '“', '‘', ')', ']', '”', '’', ',', '.', '?', '!', ';', ':')
          .Replace('’', '\'').ToLowerInvariant();
}
