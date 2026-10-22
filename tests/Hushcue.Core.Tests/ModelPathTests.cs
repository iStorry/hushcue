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
            Directory.Delete(link); // the junction only, never what it points to
        Directory.Delete(_dir);
    }

    /// <summary>A junction (no admin rights needed, unlike a symbolic link) to the cached model.</summary>
    private string LinkModel(string name)
    {
        var link = Path.Combine(_dir, name);
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, EngineTests.ModelFolder()! })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && File.Exists(Path.Combine(link, "model.bin")), errors);
        return link;
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
