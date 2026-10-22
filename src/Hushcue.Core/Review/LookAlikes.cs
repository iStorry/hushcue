using System.Text.RegularExpressions;

namespace Hushcue.Core.Review;

/// <summary>A word in a subtitle's text: where it starts, how long it is, and the word.</summary>
public sealed record TextWord(int Index, int Length, string Text);

/// <summary>
/// Words that sound the same as other words, or nearly ("calm", "come", "cum"), so Whisper can only pick the spelling
/// from the words around them, and sometimes picks the wrong one. The review offers the others as one-click swaps.
/// </summary>
public static partial class LookAlikes
{
    /// <param name="Common">has a word so common that offering swaps on every one would crowd the review: they are
    /// offered only where Whisper was not sure</param>
    /// <param name="Frequent">has a word among the most common of all, which Whisper must be even less sure of
    /// before it is flagged (UnsureWords.FrequentBelow): its doubt about "one" or "two" is mostly whether to write a
    /// number</param>
    private sealed record Group(string[] Words, bool Common, bool Frequent);

    private static readonly Group[] Groups =
    [
        .. new[]
        {
            "weather whether", "accept except", "affect effect", "break brake", "breaks brakes",
            "principal principle", "allowed aloud", "bare bear", "fair fare", "flour flower", "heal heel",
            "mail male", "pair pear", "plain plane", "rain reign rein", "sole soul", "stair stare", "steal steel",
            "tail tale", "waist waste", "loose lose", "desert dessert", "complement compliment",
            "stationary stationery", "council counsel", "cereal serial", "morning mourning", "patience patients",
            "presence presents", "guest guessed", "whose who's", "piece peace",
        }.Select(g => new Group(g.Split(' '), false, false)),
        .. new[]
        {
            "calm come cum", "their there they're", "your you're", "its it's", "then than", "whole hole",
            "threw through", "passed past", "meat meet", "son sun", "wait weight", "lead led", "role roll",
            "board bored", "course coarse", "missed mist", "made maid", "cell sell", "die dye", "knight night",
            "knot not", "nose knows", "weak week", "wear where", "hi high", "we're were", "want won't",
            "site sight cite side",
        }.Select(g => new Group(g.Split(' '), true, false)),
        .. new[]
        {
            "to too two", "for four", "no know", "one won", "by buy bye", "our hour", "would wood", "which witch",
            "can can't", "see sea", "way weigh", "new knew", "right write rite", "here hear",
        }.Select(g => new Group(g.Split(' '), true, true)),
    ];

    private static readonly Dictionary<string, Group> ByWord =
        Groups.SelectMany(g => g.Words.Select(w => (w, g))).ToDictionary(p => p.w, p => p.g);

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)*")]
    private static partial Regex WordPattern();

    /// <summary>The words of a text, with apostrophes inside them ("they're") but no punctuation around them.</summary>
    public static IEnumerable<TextWord> Words(string text) =>
        WordPattern().Matches(text).Select(m => new TextWord(m.Index, m.Length, m.Value));

    /// <summary>The words that sound like <paramref name="word"/>, written in its case ("Calm" → "Come", "Cum");
    /// none when it is not a look-alike.</summary>
    public static IReadOnlyList<string> Others(string word) =>
        ByWord.TryGetValue(Key(word), out var group)
            ? [.. group.Words.Where(w => w != Key(word)).Select(w => MatchCase(w, word))]
            : [];

    public static bool IsLookAlike(string word) => ByWord.ContainsKey(Key(word));

    /// <summary>So common that its swaps are offered only where Whisper was not sure.</summary>
    public static bool IsCommon(string word) => ByWord.TryGetValue(Key(word), out var group) && group.Common;

    /// <summary>Among the most common words of all: flagged only when Whisper was much less sure.</summary>
    public static bool IsFrequent(string word) => ByWord.TryGetValue(Key(word), out var group) && group.Frequent;

    /// <summary><paramref name="text"/> with <paramref name="word"/> replaced by <paramref name="replacement"/>.</summary>
    public static string Replace(string text, TextWord word, string replacement) =>
        string.Concat(text.AsSpan(0, word.Index), replacement, text.AsSpan(word.Index + word.Length));

    /// <summary>Lower case, with a typographic apostrophe made straight: the form the groups use.</summary>
    public static string Key(string word) => word.ToLowerInvariant().Replace('’', '\'');

    private static string MatchCase(string word, string like) =>
        like.Length > 1 && like.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? word.ToUpperInvariant()
        : like.Length > 0 && char.IsUpper(like[0]) ? char.ToUpperInvariant(word[0]) + word[1..]
        : like.Contains('’') ? word.Replace('\'', '’')
        : word;
}
