using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

/// <summary>The clean-up steps one by one; the ones that need a model are in <see cref="TranscriptionTests"/>.</summary>
public class CleanupTests
{
    private static Word W(string text, double start, double end) => new(text, start, end);

    private static List<Word> Seq(string text, double step = .5) =>
        [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((t, i) => W(" " + t, i * step, i * step + .4))];

    private static string[] Texts(IEnumerable<Word> words) => [.. words.Select(w => w.Text.Trim())];

    [Fact]
    public void StripMarkers()
    {
        List<Word> words = [W(" Hello", 0, .3), W(" (laughs)", .3, .8), W(" *sighs", .8, 1), W(" heavily*", 1, 1.4),
                            W(" [Music]", 1.4, 2), W(" 5*3", 2, 2.3), W(" ♪", 2.3, 2.4), W(" la", 2.4, 2.5),
                            W(" ♪", 2.5, 2.6), W(" yes", 2.6, 3)];
        Assert.Equal(["Hello", "5*3", "yes"], Texts(Cleanup.StripMarkers(words)));
    }

    [Fact]
    public void CuesSplitOnGapAndLength()
    {
        List<Word> words = [W(" one", 0, .5), W(" two", .5, 1), W(" three", 3, 3.5)];
        Assert.Equal(["one two", "three"], Cleanup.BuildCues(words).Select(c => c.Text));
        var longRun = Enumerable.Range(0, 30).Select(i => W($" w{i}", i * .5, i * .5 + .4));
        Assert.All(Cleanup.BuildCues(longRun), c => Assert.True(c.End - c.Start <= Cleanup.MaxCueSeconds + 0.01));
    }

    [Fact]
    public void DropJunk()
    {
        List<Cue> cues = [new(0, 1, "Thank you for watching!"), new(2, 3, "ok"), new(4, 5, "ok"),
                          new(6, 7, "Ok."), new(8, 9, "real line")];
        Assert.Equal(["ok", "ok", "real line"], Cleanup.DropJunk(cues).Select(c => c.Text));
    }

    [Fact]
    public void TimingAndSrt()
    {
        var cues = Cleanup.FixTiming([new Cue(0, 0.1, "a"), new Cue(0.5, 2, "b")]);
        Assert.True(cues[0].End - cues[0].Start >= 0.2 && cues[0].End <= cues[1].Start);
        var srt = Srt.Write([new Cue(3661.5, 3663, string.Concat(Enumerable.Repeat("x ", 30)))]);
        Assert.StartsWith("1\n01:01:01,500 --> 01:01:03,000\n", srt);
        Assert.True(srt.Split('\n').Skip(2).Max(l => l.Length) <= Cleanup.MaxLine + 1);
    }

    [Fact]
    public void UniquePath()
    {
        var folder = Directory.CreateTempSubdirectory("hushcue_test_");
        try
        {
            var file = Path.Combine(folder.FullName, "a.srt");
            File.WriteAllText(file, "x");
            Assert.Equal("a (1).srt", Path.GetFileName(FileNames.Unique(file)));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void MergeOrphans()
    {
        List<Cue> cues = [new(0, .5, "Oh,"), new(.8, 2, "my God."), new(5, 6, "Done."), new(9, 10, "ok")];
        var output = Cleanup.MergeOrphans(cues);
        Assert.Equal(["Oh, my God.", "Done.", "ok"], output.Select(c => c.Text));
        Assert.Equal((0.0, 2.0), (output[0].Start, output[0].End));
    }

    [Fact]
    public void CoverageWarnings()
    {
        Assert.NotEmpty(Cleanup.CoverageWarnings([], 100));
        Assert.NotEmpty(Cleanup.CoverageWarnings([new Cue(0, 5, "x")], 1000));
        Assert.Empty(Cleanup.CoverageWarnings([new Cue(0, 5, "x"), new Cue(90, 99, "y")], 100));
    }

    [Fact]
    public void CollapseRepeats()
    {
        var run = Enumerable.Range(0, 16).Select(i => W(" mmm,", i * .5, i * .5 + .4));
        var output = Cleanup.CollapseRepeats([W(" hi", -1, -.5), .. run, W(" yes", 9, 9.4)]);
        Assert.Equal(["hi", "mmm,", "mmm,", "mmm,", "yes"], Texts(output));
        Assert.Equal(2.0 + 1.0, output[3].End); // stretched, capped at +2.0 s from its start
        Assert.Equal(9, output[4].Start);
        var shortRun = Enumerable.Range(0, 4).Select(i => W(" yes", i, i + .5)).ToList(); // 4 in a row is legitimate speech
        Assert.Equal(shortRun, Cleanup.CollapseRepeats(shortRun));
    }

    [Fact]
    public void SquashStretched()
    {
        var output = Cleanup.SquashStretched([W(" M" + new string('m', 446), 0, 3), W(" Ahhhhh!", 3, 4), W(" 10000", 4, 5),
                                              W(" mm", 5, 6), W(" good", 6, 7)]);
        Assert.Equal([" Mmmm", " Ahhh!", " 10000", " mm", " good"], output.Select(w => w.Text));
        Assert.Equal((0.0, 3.0), (output[0].Start, output[0].End));
    }

    [Fact]
    public void CollapsePhraseLoops()
    {
        var loop = Seq(string.Concat(Enumerable.Repeat("oh, wow, ", 6))); // 12 words: 'oh, wow,' x6 -> keep x3
        Assert.Equal([.. Enumerable.Repeat(new[] { "oh,", "wow," }, 3).SelectMany(x => x)], Texts(Cleanup.CollapseRepeats(loop)));
        var three = Seq("oh wow oh wow oh wow"); // only 3 repeats: legitimate, untouched
        Assert.Equal(three, Cleanup.CollapseRepeats(three));
        var tri = Seq(string.Concat(Enumerable.Repeat("yes right here ", 5))); // 3-word phrase x5 -> x3
        Assert.Equal(9, Cleanup.CollapseRepeats(tri).Count);
        var mixed = Seq("hi oh, wow, oh, wow, oh, wow, oh, wow, bye");
        Assert.Equal(["hi", "oh,", "wow,", "oh,", "wow,", "oh,", "wow,", "bye"], Texts(Cleanup.CollapseRepeats(mixed)));
        var punct = Enumerable.Repeat(W(" ...", 0, .1), 8).ToList(); // empty keys never count as a repeat
        Assert.Equal(punct, Cleanup.CollapseRepeats(punct));
    }

    [Fact]
    public void FixTimingMergesCuesThatStartTogether()
    {
        // seen in a real run: "Yeah," and "there" both started at 25:48.2 and overlapped
        var cues = Cleanup.FixTiming([new Cue(1548.2, 1548.4, "Yeah,"), new Cue(1548.2, 1558.28, "there"),
                                      new Cue(1558.3, 1559.0, "you go.")]);
        Assert.Equal(["Yeah, there", "you go."], cues.Select(c => c.Text));
        Assert.All(cues.Zip(cues.Skip(1)), p => Assert.True(p.First.End <= p.Second.Start));
    }

    [Fact]
    public void FixTimingNeverOverlapsOnRandomInput()
    {
        var rng = new Random(1);
        for (var round = 0; round < 200; round++)
        {
            var cues = Enumerable.Range(0, rng.Next(1, 26)).Select(_ =>
            {
                var start = rng.NextDouble() * 30;
                return new Cue(start, start + 0.05 + rng.NextDouble() * 5.95, "x");
            }).ToList();
            var output = Cleanup.FixTiming(cues);
            Assert.All(output.Zip(output.Skip(1)), p => Assert.True(p.First.End <= p.Second.Start + 1e-9));
            Assert.All(output, c => Assert.True(c.End > c.Start));
        }
    }

    [Fact]
    public void ListeningHallucinationIsDropped()
    {
        List<Cue> cues = [new(0, 2, "Thank you for listening to the show."), new(3, 4, "Thank you, everyone.")];
        Assert.Equal(["Thank you, everyone."], Cleanup.DropJunk(cues).Select(c => c.Text));
    }

    [Theory]
    [InlineData(" now.", " The", Breaks.SentenceEnd)]
    [InlineData(" office,", " cancel", Breaks.ClauseEnd)]
    [InlineData(" tired", " because", Breaks.BeforeConjunction)]
    [InlineData(" sat", " on", Breaks.BeforePreposition)]
    [InlineData(" the", " meeting", Breaks.Splits)]
    [InlineData(" on", " Monday", Breaks.Splits)]
    [InlineData(" don’t", " know", Breaks.Splits)]
    [InlineData(" Maria", " Garcia", Breaks.Splits)]
    [InlineData(" 10", " minutes", Breaks.Splits)]
    [InlineData(" quickly", " walked", Breaks.Neutral)]
    public void BreakCosts(string before, string after, int cost) => Assert.Equal(cost, Breaks.Cost(before, after));

    [Theory]
    [InlineData("I need you to call the office, cancel the meeting, and book a table.",
                "I need you to call the office,\ncancel the meeting, and book a table.")]
    [InlineData("Please read all of the instructions carefully before you begin the test.",
                "Please read all of the instructions\ncarefully before you begin the test.")]
    [InlineData("She told me that the new manager would change everything about the team.",
                "She told me that the new manager\nwould change everything about the team.")]
    public void LinesBreakAtTheBestPlace(string text, string expected) => Assert.Equal(expected, Cleanup.WrapLines(text));

    [Fact]
    public void LinesStayWithinTheLimit()
    {
        var text = string.Join(' ', Enumerable.Repeat("the", 20)); // 79 characters, nothing but bad breaks
        Assert.All(Cleanup.WrapLines(text).Split('\n'), line => Assert.True(Py.Len(line) <= Cleanup.MaxLine));
    }

    [Fact]
    public void FullCueIsCutAtAClause()
    {
        var words = Seq("Honestly, I can't believe how quickly the whole weekend went by, and now it is already Monday " +
                        "morning again for all of us", step: .2);
        var cues = Cleanup.BuildCues(words);
        Assert.Equal("Honestly, I can't believe how quickly the whole weekend went by,", cues[0].Text);
        Assert.All(cues, c => Assert.True(Py.Len(c.Text) <= Cleanup.MaxCueChars));
    }

    [Fact]
    public void CuesStayLongEnoughToRead()
    {
        // 63 characters spoken in 1.5 s need 3.15 s at 20 characters per second; the next cue is far enough away
        var cues = Cleanup.FixTiming([new Cue(0, 1.5, "Where did you put the keys? Were they on the kitchen table?"),
                                      new Cue(10, 10.3, "No."), new Cue(10.9, 12, "Then where?")]);
        Assert.Equal(Py.Len(cues[0].Text) / Cleanup.MaxCharsPerSecond, cues[0].End, 3);
        Assert.Equal(10.9 - Cleanup.CueGapSeconds, cues[1].End, 3); // held for 5/6 s, but not into the next cue
        Assert.Equal(12, cues[2].End);
    }
}
