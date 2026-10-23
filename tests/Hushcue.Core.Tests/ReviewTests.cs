using Hushcue.Core.Pipeline;
using Hushcue.Core.Review;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

/// <summary>The review screen's logic: reading .srt files, times, flags, the cue editor with undo, the waveform.</summary>
public class ReviewTests
{
    private static readonly Cue[] Three =
    [
        new(1.0, 2.5, "Right, let's pick up where we left off."),
        new(3.0, 5.0, "So the key idea behind attention is"),
        new(5.2, 8.0, "that every token looks at every other token."),
    ];

    private static CueEditor Editor(double duration = 60) => new(Three, duration);

    // --- SRT

    [Fact]
    public void SrtReadsWhatItWrites()
    {
        Cue[] cues = [new(0.5, 2.25, "hello"), new(61.001, 3725.5, "a long line that the writer wraps onto two lines of text")];
        Assert.Equal(cues, Srt.Read(Srt.Write(cues)));
        Assert.Equal(cues, Srt.Read("﻿" + Srt.Write(cues).Replace("\n", "\r\n")));
    }

    [Fact]
    public void SrtReadToleratesLooseFiles()
    {
        const string text = "00:00:01.000 --> 00:00:02,500\nno number\n\n\n7\n00:01:02,5 --> 00:01:03,000\nshort ms\n\n" +
                            "8\nnot a time line\nskipped\n\n9\n00:00:05,000 --> 00:00:06,000\n\n";
        Assert.Equal([new Cue(1, 2.5, "no number"), new Cue(62.5, 63, "short ms")], Srt.Read(text));
    }

    // --- times

    [Theory]
    [InlineData(252.34, 1, "04:12.3")]
    [InlineData(59.96, 1, "01:00.0")]
    [InlineData(3852.5, 1, "1:04:12.5")]
    [InlineData(5.25, 3, "00:05.250")]
    [InlineData(-1, 0, "00:00")]
    public void CueTimeFormats(double seconds, int decimals, string expected) =>
        Assert.Equal(expected, CueTime.Format(seconds, decimals));

    [Theory]
    [InlineData("4:12.3", 252.3)]
    [InlineData("04:12,345", 252.345)]
    [InlineData("1:04:12", 3852)]
    [InlineData("252.3", 252.3)]
    [InlineData(" 00:05.250 ", 5.25)]
    public void CueTimeParses(string text, double expected)
    {
        Assert.True(CueTime.TryParse(text, out var seconds));
        Assert.Equal(expected, seconds, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("4:75")]
    [InlineData("1:2:3:4")]
    [InlineData("-3")]
    [InlineData("4::12")]
    public void CueTimeRejectsNonsense(string text) => Assert.False(CueTime.TryParse(text, out _));

    // --- flags

    [Fact]
    public void FlagsFindHallucinationsLoopsRepeatsAndGaps()
    {
        Cue[] cues =
        [
            new(0.5, 2, "Hello there."),
            new(2.5, 4, "Thanks for watching!"),
            new(4.5, 6, "Thanks for watching, and see you next time."),
            new(6.5, 8, "mmm mmm mmm mmm"),
            new(8.5, 9.5, "No, no, no."),
            new(40, 42, "We're back."),
            new(42.5, 44, "We're back!"),
        ];
        var flags = CueFlags.Find(cues, 50);
        Assert.Equal([(FlagKind.Hallucination, 1), (FlagKind.Hallucination, 2), (FlagKind.Repeat, 3), (FlagKind.Gap, -1), (FlagKind.Repeat, 6)],
                     flags.Select(f => (f.Kind, f.Cue ?? -1)));
        var gap = flags.Single(f => f.Kind == FlagKind.Gap);
        Assert.Equal((9.5, 40.0), (gap.Start, gap.End));
        Assert.Equal("No subtitles for 30 s", gap.Message);
    }

    [Fact]
    public void FlagsUseTheCoverageWarningsForTheWholeFile()
    {
        var flags = CueFlags.Find([new Cue(1, 2, "Only this.")], 600);
        var coverage = Assert.Single(flags, f => f.Kind == FlagKind.Coverage);
        Assert.StartsWith("Subtitles stop at", coverage.Message);
        Assert.Equal(2, coverage.Start);
        Assert.DoesNotContain(flags, f => f.Kind == FlagKind.Gap && f.Start == 2); // the same stretch, said once

        Assert.Equal(FlagKind.Coverage, Assert.Single(CueFlags.Find([], 10)).Kind); // "No speech was found"
    }

    [Fact]
    public void ShortSilencesAreNotGaps() => Assert.Empty(CueFlags.Find(Three, 20));

    // --- editor

    [Fact]
    public void EditsCanBeUndoneAndRedone()
    {
        var editor = Editor();
        var changes = new List<CueEdit>();
        editor.Changed += (_, e) => changes.Add(e);

        Assert.True(editor.SetText(1, "  So the key   idea is "));
        Assert.Equal("So the key idea is", editor.Cues[1].Text);
        Assert.True(editor.IsDirty);
        Assert.False(editor.SetText(1, "So the key idea is")); // nothing changed: no undo step
        Assert.True(editor.SetTimes(1, 2.9, 4.8));
        Assert.Equal("Change timing", editor.UndoName);

        Assert.True(editor.Undo());
        Assert.Equal((3.0, 5.0), (editor.Cues[1].Start, editor.Cues[1].End));
        Assert.Equal("Change timing", editor.RedoName);
        Assert.True(editor.Undo());
        Assert.Equal(Three, editor.Cues);
        Assert.False(editor.IsDirty);
        Assert.False(editor.Undo());

        Assert.True(editor.Redo());
        Assert.Equal("So the key idea is", editor.Cues[1].Text);
        Assert.Equal(new CueEdit("Edit text", 1, Undone: true), changes[^1]);
        editor.Delete(0); // a new edit drops the redo steps
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void SavingMarksTheCuesClean()
    {
        var editor = Editor();
        editor.Delete(2);
        editor.MarkSaved();
        Assert.False(editor.IsDirty);
        editor.Undo();
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void TimesStayBetweenTheNeighbours()
    {
        var editor = Editor(duration: 9);
        Assert.Equal((2.5, 5.2), editor.Limits(1));
        Assert.Equal((2.5, 5.2), editor.ClampTimes(1, 1.0, 7.0));
        Assert.Equal((4.9, 5.1), editor.ClampTimes(1, 4.9, 5.0)); // at least MinCueSeconds long
        Assert.Equal((0, 2.5), editor.ClampTimes(0, -3, 2.5));
        Assert.Equal((5.2, 9.0), editor.ClampTimes(2, 5.2, 12)); // not past the end of the file
        Assert.False(editor.SetTimes(1, 3.0, 5.0));
    }

    [Fact]
    public void SplitDividesBetweenWords()
    {
        var editor = Editor();
        // at the caret, timed by the text: 15 of its 34 letters
        Assert.Equal(2, editor.Split(1, "So the key idea".Length));
        Assert.Equal([new Cue(3.0, 3.882, "So the key idea"), new Cue(3.882, 5.0, "behind attention is")], editor.Cues.Skip(1).Take(2));
        editor.Undo();
        // at the playhead, the middle word boundary when the caret is at an end
        Assert.Equal(2, editor.Split(1, 0, at: 4.25));
        Assert.Equal(new Cue(3.0, 4.25, "So the key idea"), editor.Cues[1]);
        Assert.Equal(-1, new CueEditor([new Cue(0, 1, "Word.")], 5).Split(0, 2));
    }

    [Fact]
    public void MergeDeleteAndInsert()
    {
        var editor = Editor();
        Assert.True(editor.Merge(1));
        Assert.Equal(new Cue(3.0, 8.0, "So the key idea behind attention is that every token looks at every other token."), editor.Cues[1]);
        Assert.False(editor.Merge(1)); // nothing after it
        editor.Undo();

        Assert.True(editor.Delete(0, 2));
        Assert.Single(editor.Cues);
        editor.Undo();

        Assert.Equal(1, editor.Insert(2.6, "New subtitle")); // after cue 0, up to cue 1
        Assert.Equal(new Cue(2.6, 3.0, "New subtitle"), editor.Cues[1]);
        Assert.Equal(-1, editor.Insert(1.5, "inside cue 0 goes after it, where there is no room now"));
        Assert.Equal(4, editor.Insert(20, "later"));
        Assert.Equal(new Cue(20, 22, "later"), editor.Cues[4]);

        Assert.True(editor.SetText(4, "   ")); // emptied: deleted
        Assert.Equal(4, editor.Cues.Count);
    }

    [Fact]
    public void ReplaceRangeSwapsTheCuesOfAStretch()
    {
        var editor = Editor();
        var first = editor.ReplaceRange(3.0, 8.0, [new Cue(2.0, 4.0, "Re-run one."), new Cue(4.5, 9.5, "Re-run two.")], "Re-run");
        Assert.Equal(1, first);
        // kept after cue 0 (ends 2.5); the last one may run on, nothing follows
        Assert.Equal([Three[0], new Cue(2.5, 4.0, "Re-run one."), new Cue(4.5, 9.5, "Re-run two.")], editor.Cues);
        Assert.Equal("Re-run", editor.UndoName);
        editor.Undo();
        Assert.Equal(Three, editor.Cues);

        editor.ReplaceRange(2.5, 3.0, [new Cue(2.6, 3.4, "In a gap.")], "Re-run"); // nothing removed, kept before cue 1
        Assert.Equal(new Cue(2.6, 3.0, "In a gap."), editor.Cues[1]);
        Assert.Equal(4, editor.Cues.Count);
    }

    [Fact]
    public void LookupsFindTheCueAtATimeAndSearch()
    {
        var editor = Editor();
        Assert.Equal(-1, editor.IndexAt(0.5));
        Assert.Equal(0, editor.IndexAt(1.0));
        Assert.Equal(-1, editor.IndexAt(2.7));
        Assert.Equal(1, editor.IndexFrom(2.7));
        Assert.Equal(2, editor.IndexAt(7.99));
        Assert.Equal(-1, editor.IndexFrom(9));
        Assert.Equal([1, 2], editor.Search("TOKEN").Concat(editor.Search("attention")).Order());
        Assert.Equal([0, 1, 2], editor.Search(" "));
    }

    [Fact]
    public void FlagsFollowTheEditsAndCanBeDismissed()
    {
        var editor = new CueEditor([new Cue(1, 2, "Hello."), new Cue(3, 4, "Thanks for watching!")], 7);
        var flag = Assert.Single(editor.Flags);
        Assert.Equal(FlagKind.Hallucination, flag.Kind);
        editor.Delete(1);
        Assert.Empty(editor.Flags);
        editor.Undo();
        editor.Dismiss(editor.Flags[0]);
        Assert.Empty(editor.Flags);
    }

    // --- waveform

    [Fact]
    public void WaveformKeepsTheLowestAndHighestSampleOfEachStretch()
    {
        var samples = new float[16000 * 2]; // 2 s: silence, then a loud second
        for (var i = 16000; i < samples.Length; i++)
            samples[i] = i % 2 == 0 ? 0.5f : -0.25f;
        var wave = Waveform.Build(samples, 16000);
        Assert.Equal(200, wave.Buckets);
        Assert.Equal(2.0, wave.Duration);
        Assert.Equal(0.5f, wave.Peak);

        var (min, max) = wave.Envelope(0, 2, 4);
        Assert.Equal([0f, 0f, -0.5f, -0.5f], min);
        Assert.Equal([0f, 0f, 1f, 1f], max);

        (min, max) = wave.Envelope(1.5, 3.5, 4); // runs past the end
        Assert.Equal([1f, 0f, 0f, 0f], max);
        Assert.Empty(wave.Envelope(0, 1, 0).Max);
    }

    // --- re-run mapping

    [Fact]
    public void ReRunDecodesTheStretchWithItsContextAndTheLoopPenalty()
    {
        var audio = new float[20 * 16000];
        Word[] clip = [new(" outside", 0.2, 0.6), new(" new", 2.5, 2.9), new(" words.", 3.0, 3.5)]; // the clip starts at 3 s
        var model = new FakeTranscriber([], clip, audio.Length);
        var steps = new JobSteps
        {
            CudaDeviceCount = () => 0, FindModel = _ => "fake-model", LoadModel = (_, _) => model,
            SpeechProbabilities = FakeSpeech.Everywhere,
        };
        var log = new List<string>();

        var cues = PartTranscription.Run(audio, 5, 9, new JobSettings(), steps, log.Add, default);

        Assert.Equal([new Cue(5.5, 6.5, "new words.")], cues);
        var call = Assert.Single(model.Calls);
        Assert.Equal(8 * 16000, call.Samples); // 3..11 s: 2 s of context either side
        Assert.Equal(FileTranscription.LoopRepetitionPenalty, call.RepetitionPenalty);
        Assert.StartsWith("Re-running 00:05–00:09", log[0]);

        var missing = new JobSteps { FindModel = _ => null };
        Assert.Throws<Engine.EngineException>(() => PartTranscription.Run(audio, 5, 9, new JobSettings(), missing, _ => { }, default));
    }

    [Fact]
    public void ReRunWindowsAndMapsWordsBack()
    {
        Assert.Equal((0.0, 9.0), PartTranscription.Window(1, 7, 60));
        Assert.Equal((58.0, 60.0), PartTranscription.Window(60, 61, 60));

        Word[] clip = [new(" before", 0.5, 1.9), new(" one", 2.0, 2.4), new(" two", 2.5, 3.0), new(" after", 4.1, 4.6)];
        var words = PartTranscription.MapWords(clip, 10, 12, 14); // the clip started at 10 s; keep 12..14
        Assert.Equal(["one", "two"], words.Select(w => w.Text.Trim()));
        Assert.Equal(12.0, words[0].Start, 6);

        var cues = PartTranscription.ToCues(words, 12, 12.6);
        Assert.Equal([new Cue(12.0, 12.6, "one two")], cues); // the clean-up's minimum length is cut at the end of the stretch
    }
}
