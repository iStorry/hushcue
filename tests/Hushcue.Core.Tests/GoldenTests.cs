using System.Text.Json;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

/// <summary>
/// The clean-up must keep writing exactly these SRTs from these words: 60 synthetic word lists built to hit every
/// branch (markers, loops, stretched hums, hallucinations, odd spacing, non-ASCII). Change a case only when the
/// clean-up is meant to change.
/// </summary>
public class GoldenTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Golden"), "synthetic-*.json").Order())
            data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CleanupWritesTheGoldenSrt(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var words = json.RootElement.GetProperty("words").EnumerateArray()
            .Select(w => new Word(w.GetProperty("word").GetString()!, w.GetProperty("start").GetDouble(), w.GetProperty("end").GetDouble()))
            .ToList();
        var expected = json.RootElement.GetProperty("srt").GetString();

        var actual = Srt.Write(Cleanup.ToCues(Cleanup.SquashStretched(Cleanup.StripMarkers(words))));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SyntheticCasesArePresent() =>
        Assert.Equal(60, Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Golden"), "synthetic-*.json").Length);
}
