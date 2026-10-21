namespace Hushcue.Core.Pipeline;

/// <summary>Where the CTranslate2 Whisper models live on this machine.</summary>
public static class Models
{
    /// <summary>Model name to its Hugging Face repository (the ones faster-whisper downloads).</summary>
    public static readonly IReadOnlyDictionary<string, string> Repositories = new Dictionary<string, string>
    {
        ["large-v3-turbo"] = "mobiuslabsgmbh/faster-whisper-large-v3-turbo",
        ["large-v3"] = "Systran/faster-whisper-large-v3",
    };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hushcue", "models");

    /// <summary>
    /// The folder holding <paramref name="name"/>: Hushcue's own models folder first, then the Hugging Face cache
    /// that faster-whisper (and so SubGen) fills. Null when the model is not downloaded.
    /// </summary>
    public static string? Find(string name)
    {
        var own = Path.Combine(Folder, name);
        if (IsModel(own))
            return own;
        if (!Repositories.TryGetValue(name, out var repository))
            return null;
        var snapshots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache",
                                     "huggingface", "hub", "models--" + repository.Replace("/", "--"), "snapshots");
        return Directory.Exists(snapshots) ? Directory.EnumerateDirectories(snapshots).FirstOrDefault(IsModel) : null;
    }

    private static bool IsModel(string folder) =>
        File.Exists(Path.Combine(folder, "model.bin")) && File.Exists(Path.Combine(folder, "tokenizer.json"));
}
