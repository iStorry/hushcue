using System.Diagnostics;
using Hushcue.Core.Engine;

namespace Hushcue.Core.Tests;

/// <summary>
/// Models in folders whose path is not ASCII (a user name like "José" or "山田" puts them there). CTranslate2 4.8.2
/// takes the path as UTF-8 and opens files through UTF-16 on Windows (filesystem.cc), and the bridge passes UTF-8.
/// </summary>
public sealed class ModelPathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hushcue_模型_modèle_").FullName;

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateDirectories(_dir))
            Directory.Delete(link); // the link only, never what it points to
        Directory.Delete(_dir);
    }

    /// <summary>A link to the cached model: on Windows a junction (no admin rights needed, unlike a symbolic link).</summary>
    private string LinkModel(string name)
    {
        var model = EngineTests.ModelFolder() ?? throw new InvalidOperationException("ModelFact runs this only with a model");
        var link = Path.Combine(_dir, name);
        if (OperatingSystem.IsWindows())
            MakeJunction(link, model);
        else
            Directory.CreateSymbolicLink(link, model);
        Assert.True(File.Exists(Path.Combine(link, "model.bin")));
        return link;
    }

    private static void MakeJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("cmd.exe did not start");
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors);
    }

    [ModelFact]
    public void LoadsAModelFromANonAsciiPath()
    {
        foreach (var name in new[] { "modèle", "日本語 モデル" }) // one the ANSI code page can spell, one it cannot
        {
            var path = LinkModel(name);
            using var model = WhisperModel.Load(path, "cpu", "int8");
            Assert.Equal(128, model.NMels);
            Assert.True(model.IsMultilingual);
            _ = new Transcriber(model); // reads tokenizer.json and preprocessor_config.json from the same folder
        }
    }
}
