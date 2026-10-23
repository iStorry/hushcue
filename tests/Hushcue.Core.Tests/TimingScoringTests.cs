using Hushcue.Core.Evaluation;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

public class TimingScoringTests
{
    private static readonly List<Cue> Phrases = [new(1, 3, "one two"), new(5, 6, "three")];

    [Fact]
    public void EachPhraseIsTimedByItsFirstAndLastWord()
    {
        var score = TimingScoring.Compare(Phrases, [new(" one", 0.6, 1.5), new(" two", 1.6, 2.9), new(" three", 5.2, 6.3)]);
        Assert.Equal(2, score.Heard);
        Assert.Equal([-0.4, 0.2], score.StartErrors.Select(e => Math.Round(e, 3)));
        Assert.Equal([-0.1, 0.3], score.EndErrors.Select(e => Math.Round(e, 3)));
    }

    [Fact]
    public void WordsFarFromEveryPhraseAndPhrasesWithoutWordsAreLeftOut()
    {
        var score = TimingScoring.Compare(Phrases, [new(" thank", 9, 9.3), new(" one", 1, 1.4)]);
        Assert.Equal(1, score.Heard);
        Assert.Null(score.Phrases[1].StartError);
        Assert.Empty(TimingScoring.Compare([], [new(" one", 1, 2)]).Phrases);
    }

    [Fact]
    public void Median() =>
        Assert.Equal((2.0, 2.5, 0.0), (TimingScoring.Median([3, 1, 2]), TimingScoring.Median([4, 1, 2, 3]), TimingScoring.Median([])));
}
