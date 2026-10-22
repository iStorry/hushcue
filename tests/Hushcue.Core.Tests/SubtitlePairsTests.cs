using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

public sealed class SubtitlePairsTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("hushcue_pairs_").FullName;

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    private string Touch(string name)
    {
        var path = Path.Combine(_tmp, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void FindsTheSubtitlesNextToAVideo()
    {
        var video = Touch("talk.mp4");
        Assert.Null(SubtitlePairs.FindSubtitles(video));
        var tagged = Touch("talk.en.srt");
        Assert.Equal(tagged, SubtitlePairs.FindSubtitles(video));
        var plain = Touch("talk.srt");
        Assert.Equal(plain, SubtitlePairs.FindSubtitles(video)); // "name.srt" wins over "name.en.srt"
    }

    [Fact]
    public void FindsTheVideoAnSrtBelongsTo()
    {
        var srt = Touch("talk.en.srt");
        Assert.Null(SubtitlePairs.FindVideo(srt));
        var video = Touch("talk.mkv");
        Assert.Equal(video, SubtitlePairs.FindVideo(srt));
    }

    [Fact]
    public void SplitPairsSubtitlesWithTheirVideosAndQueuesTheRest()
    {
        var talk = Touch("talk.mp4");
        var talkSrt = Touch("talk.srt");
        var other = Touch("other.mp4");
        var onDisk = Touch("lecture.mkv"); // not passed, found next to its .srt
        var lectureSrt = Touch("lecture.en.srt");
        var orphan = Touch("lonely.srt");

        var (pairs, orphans, rest) = SubtitlePairs.Split([talk, talkSrt, other, lectureSrt, orphan]);

        Assert.Equal([new SubtitlePair(talk, talkSrt), new SubtitlePair(onDisk, lectureSrt)], pairs);
        Assert.Equal([orphan], orphans);
        Assert.Equal([other], rest); // a video opened with its subtitles is not queued again
    }

    [Fact]
    public void RecognisesTypes()
    {
        Assert.True(SubtitlePairs.IsSubtitle("a.SRT"));
        Assert.False(SubtitlePairs.IsSubtitle("a.vtt"));
        Assert.True(SubtitlePairs.IsMedia("a.MKV"));
        Assert.False(SubtitlePairs.IsMedia("a.srt"));
    }
}
