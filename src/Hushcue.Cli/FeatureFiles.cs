using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Hushcue.Cli;

/// <summary>
/// faster-whisper's own log-mel features, recorded by tools/bench/fw_features.py as DIR/KEY.f32 where KEY is the first
/// 16 hex digits of the SHA-256 of the float32 samples they were computed from. Fed to the engine in place of its own
/// features (<see cref="Hushcue.Core.Engine.Transcriber.FeatureSource"/>) so decoding can be compared bit for bit.
/// </summary>
internal static class FeatureFiles
{
    public static string Key(float[] audio) =>
        Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(audio.AsSpan())))[..16];

    public static Func<float[], (float[], int)> Source(string folder, int nMels) => audio =>
    {
        var path = Path.Combine(folder, Key(audio) + ".f32");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No faster-whisper features in {folder} for this audio ({audio.Length} samples); "
                + "it differs from what faster-whisper decoded", path);
        var data = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path)).ToArray();
        return (data, data.Length / nMels);
    };
}
