using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Review;

/// <summary>A word Whisper was not sure of (<see cref="UnsureWords"/>), in the file's time.</summary>
/// <param name="Probability">how sure Whisper was, 0..1</param>
/// <param name="Before">the word said before it (lower case), which tells it apart from the same word elsewhere in a
/// subtitle; null: any</param>
/// <param name="After">the word said after it; null: any</param>
public sealed record UnsureWord(string Text, double Start, double End, double Probability, string? Before = null,
                                string? After = null);

/// <summary>
/// The look-alike words (<see cref="LookAlikes"/>) Whisper was not sure of, which the review flags as "not sure". In
/// the test set (eval/lookalike) every look-alike it got wrong but one was below <see cref="Below"/>, and the words it
/// got right were at 1.0 in the middle. Kept per .srt in %LOCALAPPDATA%\Hushcue\unsure, so a review opened later
/// still has them; never next to the user's files.
/// </summary>
public static class UnsureWords
{
    public const double Below = 0.9;

    /// <summary>The threshold for the very common look-alikes (<see cref="LookAlikes.IsFrequent"/>).</summary>
    public const double FrequentBelow = 0.5;

    /// <summary>How far a word may be from a subtitle's times and still belong to it (timing edits move cues a little).</summary>
    public const double Slack = 0.5;

    private const int MaxFiles = 300;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                "Hushcue", "unsure");

    public static List<UnsureWord> Find(IReadOnlyList<Word> words)
    {
        var found = new List<UnsureWord>();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.Probability >= Below || LookAlikes.Words(w.Text).FirstOrDefault()?.Text is not { } text
                || !IsUnsure(text, w.Probability))
                continue;
            var before = i > 0 ? LookAlikes.Words(words[i - 1].Text).LastOrDefault()?.Text : null;
            var after = i + 1 < words.Count ? LookAlikes.Words(words[i + 1].Text).FirstOrDefault()?.Text : null;
            found.Add(new UnsureWord(text, w.Start, w.End, Math.Round(w.Probability, 3),
                                     before is null ? null : LookAlikes.Key(before), after is null ? null : LookAlikes.Key(after)));
        }
        return found;
    }

    /// <summary>A look-alike word said with this probability is flagged.</summary>
    public static bool IsUnsure(string word, double probability) =>
        LookAlikes.IsLookAlike(word) && probability < (LookAlikes.IsFrequent(word) ? FrequentBelow : Below);

    /// <summary>
    /// Which of the cue's words are the unsure ones: for each unsure word, the occurrence of it with the same words
    /// around it (a word at the cue's edge matches any neighbour). A cue can say "come" twice with only one in doubt,
    /// and once that one is changed the other is not flagged in its place.
    /// </summary>
    public static List<(TextWord Word, UnsureWord Unsure)> Occurrences(Cue cue, IEnumerable<UnsureWord> unsure)
    {
        var words = LookAlikes.Words(cue.Text).ToList();
        bool Fits(string? neighbour, int i) =>
            neighbour is null || i < 0 || i >= words.Count || LookAlikes.Key(words[i].Text) == neighbour;
        var found = new List<(TextWord, UnsureWord)>();
        foreach (var u in In(cue, unsure))
        {
            var at = words.FindIndex(w => LookAlikes.Key(w.Text) == LookAlikes.Key(u.Text) && found.All(f => f.Item1 != w)
                                          && Fits(u.Before, words.IndexOf(w) - 1) && Fits(u.After, words.IndexOf(w) + 1));
            if (at >= 0)
                found.Add((words[at], u));
        }
        return found;
    }

    /// <summary>The unsure words <paramref name="cue"/> still says, at about its time.</summary>
    public static List<UnsureWord> In(Cue cue, IEnumerable<UnsureWord> unsure)
    {
        var said = LookAlikes.Words(cue.Text).Select(w => LookAlikes.Key(w.Text)).ToHashSet();
        return [.. unsure.Where(u => u.Start < cue.End + Slack && u.End > cue.Start - Slack && said.Contains(LookAlikes.Key(u.Text)))];
    }

    /// <summary>Remember the unsure words of <paramref name="srt"/>; best effort (a failure only loses the flags).</summary>
    public static void Save(string srt, IReadOnlyList<UnsureWord> words, string? folder = null)
    {
        folder ??= Folder;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(FileFor(srt, folder), JsonSerializer.Serialize(new Entry(Path.GetFullPath(srt), [.. words])));
            foreach (var old in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFiles))
                old.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The unsure words remembered for <paramref name="srt"/>, or none.</summary>
    public static List<UnsureWord> Load(string srt, string? folder = null)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(FileFor(srt, folder ?? Folder)));
            return entry is not null && string.Equals(entry.Srt, Path.GetFullPath(srt), StringComparison.OrdinalIgnoreCase)
                ? entry.Words
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private sealed record Entry(string Srt, List<UnsureWord> Words);

    private static string FileFor(string srt, string folder)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(srt).ToLowerInvariant()));
        return Path.Combine(folder, Convert.ToHexString(hash, 0, 8) + ".json");
    }
}
