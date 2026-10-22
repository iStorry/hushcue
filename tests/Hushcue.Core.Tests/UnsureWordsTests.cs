using Hushcue.Core.Review;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

public sealed class UnsureWordsTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("hushcue_unsure_").FullName;

    public void Dispose() => Directory.Delete(_tmp, true);

    [Fact]
    public void OthersKeepTheWordsCase()
    {
        Assert.Equal(["come", "cum"], LookAlikes.Others("calm"));
        Assert.Equal(["Calm", "Cum"], LookAlikes.Others("Come"));
        Assert.Equal(["THEIR", "THEY'RE"], LookAlikes.Others("THERE"));
        Assert.Equal(["their", "there"], LookAlikes.Others("they’re").Select(w => w.Replace('’', '\'')));
        Assert.Empty(LookAlikes.Others("soup"));
        Assert.True(LookAlikes.IsFrequent("to"));
        Assert.False(LookAlikes.IsFrequent("calm"));
        Assert.True(LookAlikes.IsCommon("calm"));
        Assert.False(LookAlikes.IsCommon("principle"));
    }

    [Fact]
    public void ReplaceSwapsOneWordAndKeepsThePunctuation()
    {
        const string text = "Come on, stay calm. Calm down!";
        var words = LookAlikes.Words(text).ToList();
        Assert.Equal(["Come", "on", "stay", "calm", "Calm", "down"], words.Select(w => w.Text));
        Assert.Equal("Come on, stay come. Calm down!", LookAlikes.Replace(text, words[3], "come"));
        Assert.Equal(["they're", "it’s"], LookAlikes.Words("(they're) it’s").Select(w => w.Text));
    }

    [Fact]
    public void FindKeepsLookAlikesBelowTheThreshold()
    {
        List<Word> words = [new(" Stay", 0, 0.3, 0.2), new(" calm.", 0.3, 0.6, 0.61), new(" Come", 1, 1.2, 0.95),
                            new(" their", 2, 2.2), new(" there,", 3, 3.2, 0.5), new(" two", 4, 4.2, 0.6), new(" to", 5, 5.1, 0.3)];
        // "two" is a frequent look-alike: flagged only below 0.5
        Assert.Equal([new UnsureWord("calm", 0.3, 0.6, 0.61, "stay", "come"), new UnsureWord("there", 3, 3.2, 0.5, "their", "two"),
                      new UnsureWord("to", 5, 5.1, 0.3, "two", null)],
                     UnsureWords.Find(words));
    }

    [Fact]
    public void ACueIsFlaggedWhileItStillSaysTheWord()
    {
        List<UnsureWord> unsure = [new("calm", 1.2, 1.5, 0.6), new("there", 5.1, 5.3, 0.7)];
        List<Cue> cues = [new(1, 3, "Stay calm and come in."), new(4, 6, "It is over here.")];

        var flags = CueFlags.Find(cues, 10, unsure);
        var flag = Assert.Single(flags);
        Assert.Equal((FlagKind.Unsure, 0, "not sure"), (flag.Kind, flag.Cue, flag.Label));
        Assert.Contains("“come” or “cum”", flag.Message);

        // swapped: nothing left to flag
        Assert.Empty(CueFlags.Find([cues[0] with { Text = "Stay come and come in." }, cues[1]], 10, unsure));
    }

    [Fact]
    public void OnlyTheOccurrenceNearestTheUnsureWordIsMarked()
    {
        var cue = new Cue(10, 14, "Come and see it, the gallery come reading room.");
        List<UnsureWord> unsure = [new UnsureWord("come", 12.6, 12.9, 0.54, "gallery", "reading")];
        var marked = UnsureWords.Occurrences(cue, unsure);
        Assert.Equal(cue.Text.IndexOf("come", StringComparison.Ordinal), Assert.Single(marked).Word.Index);

        // changed: the "Come" at the start has other words around it, so it does not take its place
        var changed = cue with { Text = "Come and see it, the gallery cum reading room." };
        Assert.Empty(UnsureWords.Occurrences(changed, unsure));
        Assert.Empty(CueFlags.Find([changed], 20, unsure));
    }

    [Fact]
    public void TheEditorDropsTheFlagWhenTheWordIsSwapped()
    {
        var editor = new CueEditor([new Cue(1, 3, "Stay calm.")], 10, [new UnsureWord("calm", 1.2, 1.5, 0.6)]);
        Assert.Equal(FlagKind.Unsure, Assert.Single(editor.Flags).Kind);
        editor.SetText(0, "Stay come.");
        Assert.Empty(editor.Flags);
        editor.Undo();
        Assert.Single(editor.Flags);
    }

    [Fact]
    public void SavedWordsComeBackForTheSameSrtOnly()
    {
        var srt = Path.Combine(_tmp, "talk.srt");
        var store = Path.Combine(_tmp, "store");
        List<UnsureWord> words = [new("calm", 1.2, 1.5, 0.6)];
        UnsureWords.Save(srt, words, store);
        Assert.Equal(words, UnsureWords.Load(srt, store));
        Assert.Equal(words, UnsureWords.Load(srt.ToUpperInvariant(), store));
        Assert.Empty(UnsureWords.Load(Path.Combine(_tmp, "other.srt"), store));
        Assert.Empty(UnsureWords.Load(srt, Path.Combine(_tmp, "missing")));
    }
}
