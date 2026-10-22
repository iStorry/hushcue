using Hushcue.Core.Evaluation;

namespace Hushcue.Core.Tests;

public class WordScoringTests
{
    private static List<(string, double)> Said(string text) => [.. WordScoring.Words(text).Select((w, i) => (w, (double)i))];

    [Fact]
    public void WordsDropPunctuationButKeepApostrophes()
    {
        Assert.Equal(["it's", "a", "gallery", "cum", "reading", "room", "café"],
                     WordScoring.Words("It’s a gallery-cum-reading room... \"Café!\""));
        Assert.Equal(["its", "it's"], WordScoring.Words("Its, 'it's'"));
    }

    [Fact]
    public void ReferenceMarksStarredWords()
    {
        var reference = WordScoring.Reference("Stay *calm*, and *come* in.");
        Assert.Equal(["stay", "calm", "and", "come", "in"], reference.Select(w => w.Text));
        Assert.Equal([false, true, false, true, false], reference.Select(w => w.Scored));
    }

    [Fact]
    public void APerfectTranscriptScoresFull()
    {
        var score = WordScoring.Compare(WordScoring.Reference("Stay *calm* and *come* in."), Said("stay calm and come in"));
        Assert.Equal((5, 0, 2, 2), (score.Words, score.Errors, score.Scored, score.ScoredRight));
        Assert.Empty(score.Misses);
    }

    [Fact]
    public void SubstitutionsDeletionsAndInsertionsCount()
    {
        var score = WordScoring.Compare(WordScoring.Reference("Stay *calm* and *come* in."), Said("stay come and well in now"));
        // calm -> come, come -> well, plus "now"
        Assert.Equal(3, score.Errors);
        Assert.Equal(0, score.ScoredRight);
        Assert.Equal([("calm", "come"), ("come", "well")], score.Misses.Select(m => (m.Expected, m.Got)));

        var missing = WordScoring.Compare(WordScoring.Reference("Stay *calm* and *come* in."), Said("stay calm and in"));
        Assert.Equal(1, missing.Errors);
        Assert.Equal(("come", (string?)null, 2.0), (missing.Misses[0].Expected, missing.Misses[0].Got, missing.Misses[0].At));
        Assert.Equal(0.2, missing.ErrorRate, 6);
    }
}
