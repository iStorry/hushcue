using System.Text.RegularExpressions;
using Hushcue.Core.Media;

namespace Hushcue.Core.Tests;

public class InstallerTests
{
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hushcue.slnx")))
                return Path.Combine(dir.FullName, relative);
        }
        throw new FileNotFoundException("Hushcue.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheExplorerMenuCoversTheTypesTheAppOpens()
    {
        var script = File.ReadAllText(RepoFile(Path.Combine("installer", "hushcue.iss")));
        var match = Regex.Match(script, @"#dim Ext\[(\d+)\] \{([^}]*)\}");
        Assert.True(match.Success, "installer/hushcue.iss has no #dim Ext[...] list");
        var extensions = Regex.Matches(match.Groups[2].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(MediaFiles.Extensions, extensions);
        Assert.Equal(extensions.Count, int.Parse(match.Groups[1].Value));
    }
}
