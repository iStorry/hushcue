using System.Text.Json;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

/// <summary>
/// The C# clean-up must write exactly the SRT SubGen writes from the same words. Cases come from
/// tools/bench/make_golden.py: synthetic ones in Golden/ (always), real transcripts in bench-out/golden/ (when present).
/// </summary>
public class GoldenTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        var folders = new[] { Path.Combine(AppContext.BaseDirectory, "Golden"), RealCasesFolder() };
        foreach (var folder in folders.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(folder!, folder == folders[0] ? "synthetic-*.json" : "*.json").Order())
                data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CleanupMatchesSubGen(string file)
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

    private static string? RealCasesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hushcue.slnx")))
                return Path.Combine(dir.FullName, "bench-out", "golden");
        }
        return null;
    }
}
