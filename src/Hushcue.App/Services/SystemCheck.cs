using Hushcue.Core.Engine;
using Hushcue.Core.Media;
using Hushcue.Core.Pipeline;
using Microsoft.Win32;

namespace Hushcue.Services;

/// <summary>What this PC has for Hushcue: an NVIDIA GPU with CUDA, ffmpeg (with NVENC), the speech model.</summary>
internal static class SystemCheck
{
    // Display adapters (GUID_DEVCLASS_DISPLAY); readable without admin rights, unlike asking the driver.
    private const string DisplayAdapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>The model the default preset uses: the one the setup screen makes sure is on this PC.</summary>
    public static string DefaultModel => Presets.Get(Presets.Default).Model;

    /// <summary>"NVIDIA GeForce RTX 4070", or null without an NVIDIA display adapter.</summary>
    public static string? NvidiaGpuName()
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(DisplayAdapters);
            foreach (var name in adapters?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var adapter = adapters!.OpenSubKey(name);
                    if (adapter?.GetValue("ProviderName") is string provider && provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                        && adapter.GetValue("DriverDesc") is string description)
                        return description.Trim();
                }
                catch (System.Security.SecurityException)
                {
                    // some subkeys of the class are for the system only
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return null;
    }

    /// <summary>"RTX 4070" from "NVIDIA GeForce RTX 4070": short enough for the title bar.</summary>
    public static string ShortGpuName(string name) =>
        name.Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase).Replace("GeForce", "", StringComparison.OrdinalIgnoreCase).Trim();

    /// <summary>How many CUDA devices the engine sees, or why the engine could not be loaded at all.</summary>
    public static (int Devices, string? Error) Cuda()
    {
        try
        {
            return (WhisperModel.CudaDeviceCount(), null);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return (0, "The speech engine's libraries are missing: " + e.Message);
        }
    }

    public static bool HasFfmpeg()
    {
        try
        {
            _ = Tools.Ffmpeg;
            _ = Tools.Ffprobe;
            return true;
        }
        catch (MediaException)
        {
            return false;
        }
    }

    /// <summary>Whether burning can use the GPU encoder; runs a tiny test encode once. Needs ffmpeg.</summary>
    public static bool HasNvenc() => ReferenceEquals(Embed.PickEncoder(), Embed.NvencArgs);

    /// <summary>
    /// When set (HUSHCUE_SETUP=1), the setup screen opens at start as on a fresh PC and offers to download the model
    /// even if one is found: for trying the screen and the download on a set-up machine.
    /// </summary>
    public static bool ForceSetup => Environment.GetEnvironmentVariable("HUSHCUE_SETUP") == "1";

    public static string? FindDefaultModel() => ForceSetup ? null : Models.Find(DefaultModel);
}
