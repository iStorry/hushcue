namespace Hushcue.Core.Pipeline;

/// <summary>A file of a model, with the size and SHA-256 it must have.</summary>
public sealed record ModelFile(string Name, long Size, string Sha256);

/// <summary>A model on Hugging Face, pinned to a commit so the files and their checksums never change.</summary>
/// <param name="CacheRepository">the repository name faster-whisper caches the model under</param>
public sealed record ModelSource(string Repository, string Revision, string CacheRepository, IReadOnlyList<ModelFile> Files)
{
    public long Size => Files.Sum(f => f.Size);
}

/// <summary>Where the CTranslate2 Whisper models come from and where they live on this machine.</summary>
public static class Models
{
    /// <summary>The models faster-whisper 1.2.1 downloads, with the files it fetches (its allow_patterns).</summary>
    public static readonly IReadOnlyDictionary<string, ModelSource> Sources = new Dictionary<string, ModelSource>
    {
        // mobiuslabsgmbh's repository moved to dropbox-dash; Hugging Face redirects the old name
        ["large-v3-turbo"] = new("dropbox-dash/faster-whisper-large-v3-turbo", "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf",
            "mobiuslabsgmbh/faster-whisper-large-v3-turbo",
        [
            new("config.json", 2263, "b0253ea6c0d3bea6b1e19e91a02acfd3b53f4467362efcb5a3e6b16c9b3a9b7e"),
            new("preprocessor_config.json", 340, "7ccc62c6f2765af1f3b46c00c9b5894426835a05021c8b9c01eecb6dfb542711"),
            new("tokenizer.json", 2710337, "297b13372ac43916285644fb9687add3cc62ee2a1adb60da3dc25cc94c1871fd"),
            new("vocabulary.json", 1068114, "c69260f2ab26d659b7c398f9a2b2b48ed0df16c3b47d7326782fd9cba71690c1"),
            new("model.bin", 1617884929, "e76620f83d5f5b69efd3d87e3dc180c1bd21df9fbebacfd4335e5e1efcc018da"),
        ]),
        ["large-v3"] = new("Systran/faster-whisper-large-v3", "edaa852ec7e145841d8ffdb056a99866b5f0a478",
            "Systran/faster-whisper-large-v3",
        [
            new("config.json", 2394, "a9306624f5ec14270a014b647e5c316b6e03a662c369758d1b90697a7b0655b9"),
            new("preprocessor_config.json", 340, "7ccc62c6f2765af1f3b46c00c9b5894426835a05021c8b9c01eecb6dfb542711"),
            new("tokenizer.json", 2480617, "6d8cbd7cd0d8d5815e478dac67b85a26bbe77c1f5e0c6d76d1ce2abc0e5f21ca"),
            new("vocabulary.json", 1068114, "c69260f2ab26d659b7c398f9a2b2b48ed0df16c3b47d7326782fd9cba71690c1"),
            new("model.bin", 3087284237, "69f74147e3334731bc3a76048724833325d2ec74642fb52620eda87352e3d4f1"),
        ]),
    };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hushcue", "models");

    /// <summary>
    /// The folder holding <paramref name="name"/>: Hushcue's own models folder first, then the Hugging Face cache
    /// that faster-whisper fills, which saves a download. Null when the model is not downloaded.
    /// </summary>
    public static string? Find(string name)
    {
        var own = Path.Combine(Folder, name);
        if (IsModel(own))
            return own;
        if (!Sources.TryGetValue(name, out var source))
            return null;
        var snapshots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache",
                                     "huggingface", "hub", "models--" + source.CacheRepository.Replace("/", "--"), "snapshots");
        return Directory.Exists(snapshots) ? Directory.EnumerateDirectories(snapshots).FirstOrDefault(IsModel) : null;
    }

    private static bool IsModel(string folder) =>
        File.Exists(Path.Combine(folder, "model.bin")) && File.Exists(Path.Combine(folder, "tokenizer.json"));
}
