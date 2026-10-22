using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Hushcue.Core.Pipeline;

public sealed class RuntimeDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A file taken out of a wheel: its path inside the wheel, its name in the runtime folder, size and SHA-256.</summary>
/// <param name="Required">false for files the engine does not load (the licence text)</param>
public sealed record RuntimeFile(string Entry, string Name, long Size, string Sha256, bool Required = true);

/// <summary>A wheel on PyPI, pinned by version, URL and SHA-256, and the files Hushcue takes out of it.</summary>
public sealed record RuntimePackage(string Name, string Version, Uri Url, long Size, string Sha256,
                                   IReadOnlyList<RuntimeFile> Files)
{
    public string FileName => Url.Segments[^1];
}

/// <summary>Which of the GPU runtime packages this machine still needs.</summary>
public sealed record RuntimeStatus(IReadOnlyList<RuntimePackage> Missing)
{
    public bool IsReady => Missing.Count == 0;

    /// <summary>Bytes <see cref="RuntimeDownload.DownloadAsync(Action{long, long}, CancellationToken)"/> will fetch.</summary>
    public long DownloadSize => Missing.Sum(p => p.Size);
}

/// <summary>
/// The NVIDIA libraries CTranslate2 needs to run on the GPU (cuBLAS, and NVRTC which cuBLASLt can load), fetched on
/// first run instead of shipping in the installer: the wheels NVIDIA publishes on PyPI, pinned by version and SHA-256,
/// with only the DLLs Hushcue needs unpacked into %LOCALAPPDATA%\Hushcue\runtime. A cancelled or failed download
/// resumes on the next call. CTranslate2 itself (and its CPU path) ships with the app, so Hushcue works without these.
/// </summary>
public static class RuntimeDownload
{
    private const int BufferSize = 1 << 20;
    private const long ReportEveryBytes = 8 << 20;
    private const string StagingSuffix = ".download";
    private const string PartialSuffix = ".part";
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// The wheels the CTranslate2 4.8.2 Windows build needs on the GPU. It is built without cuDNN and loads cuBLAS at
    /// runtime; cuBLASLt can load NVRTC. Versions are the ones SubGen runs with (pip's choice for ctranslate2 4.8.2).
    /// </summary>
    public static readonly IReadOnlyList<RuntimePackage> GpuPackages =
    [
        new("nvidia-cublas-cu12", "12.9.2.10",
            new Uri("https://files.pythonhosted.org/packages/20/e2/fc9a0e985249d873150276d5afb02e39a66817fedbf1a385724393e505ed/nvidia_cublas_cu12-12.9.2.10-py3-none-win_amd64.whl"),
            553162896, "623f43027d40d44ceadf0043f002bd25cf353e8f13ce90b9a87057019f560661",
        [
            new("nvidia/cublas/bin/cublas64_12.dll", "cublas64_12.dll", 102518272,
                "52ce5ba0ec5327d39be6021f0f9362d2ed4d64d7206f9b5afc06398d827b8e82"),
            new("nvidia/cublas/bin/cublasLt64_12.dll", "cublasLt64_12.dll", 668673536,
                "71705a7cf0923f4ab034d0d2620bbfc989ff669ac3d79f868b7a7c597ca559c5"),
            new("nvidia_cublas_cu12-12.9.2.10.dist-info/licenses/License.txt", "NVIDIA-CUDA-LICENSE.txt", 59262,
                "ad6f5853fba0ca0d159d0f58d49ae49830c2f8c93f7a92648b9ce90adb4c6ccd", Required: false),
        ]),
        new("nvidia-cuda-nvrtc-cu12", "12.9.86",
            new Uri("https://files.pythonhosted.org/packages/52/de/823919be3b9d0ccbf1f784035423c5f18f4267fb0123558d58b813c6ec86/nvidia_cuda_nvrtc_cu12-12.9.86-py3-none-win_amd64.whl"),
            76408187, "72972ebdcf504d69462d3bcd67e7b81edd25d0fb85a2c46d3ea3517666636349",
        [
            new("nvidia/cuda_nvrtc/bin/nvrtc64_120_0.dll", "nvrtc64_120_0.dll", 89832960,
                "0cc63349fedffeec1660b30955e8028b6e4e4c26cf1e41c56dc4569fd5eca82c"),
            new("nvidia/cuda_nvrtc/bin/nvrtc-builtins64_129.dll", "nvrtc-builtins64_129.dll", 7217664,
                "c405226b0b394ed8bdc593bb1419b94a989f93248ed2873e8f2e0e27c1688ca1"),
        ]),
    ];

    /// <summary>Where the downloaded libraries go (on the DLL search path, see <c>RuntimeLibraries</c>).</summary>
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hushcue", "runtime");

    /// <summary>The runtime folder shipped next to the app (CTranslate2 and its OpenMP library).</summary>
    public static string BundledFolder => Path.Combine(AppContext.BaseDirectory, "runtime");

    /// <summary>
    /// Which GPU packages are missing, looking next to the app and in <see cref="Folder"/>. Only checks that each DLL
    /// is there with the right size (fast enough for every start); the checksum was checked when it was unpacked.
    /// </summary>
    public static RuntimeStatus Check() => Check(GpuPackages, [BundledFolder, Folder]);

    internal static RuntimeStatus Check(IReadOnlyList<RuntimePackage> packages, IReadOnlyList<string> folders) =>
        new(packages.Where(p => !p.Files.Where(f => f.Required).All(f => folders.Any(dir => HasFile(dir, f)))).ToList());

    /// <summary>Download and unpack whatever <see cref="Check()"/> reports missing. Does nothing when it is all there.</summary>
    /// <param name="progress">bytes downloaded so far, and the total to download; unpacking follows the last report</param>
    public static Task DownloadAsync(Action<long, long> progress, CancellationToken cancel = default) =>
        DownloadAsync(Check().Missing, Folder, Http, progress, cancel);

    internal static async Task DownloadAsync(IReadOnlyList<RuntimePackage> packages, string folder, HttpClient http,
                                             Action<long, long> progress, CancellationToken cancel)
    {
        var missing = Check(packages, [folder]).Missing;
        if (missing.Count == 0)
            return;
        var staging = folder.TrimEnd('\\', '/') + StagingSuffix;
        var total = missing.Sum(p => p.Size);
        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(staging);
            CheckFreeSpace(missing, staging);
            var done = 0L;
            foreach (var package in missing)
            {
                var wheel = Path.Combine(staging, package.FileName);
                if (!File.Exists(wheel))
                    await DownloadWheelAsync(http, package, wheel, written => progress(done + written, total), cancel);
                done += package.Size;
                progress(done, total);
                await ExtractAsync(package, wheel, folder, cancel);
                File.Delete(wheel);
            }
            if (!Directory.EnumerateFileSystemEntries(staging).Any())
                Directory.Delete(staging);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new RuntimeDownloadException(
                $"Could not download the GPU libraries: {e.Message} Try again; the download continues where it stopped.", e);
        }
    }

    private static bool HasFile(string folder, RuntimeFile file)
    {
        var info = new FileInfo(Path.Combine(folder, file.Name));
        return info.Exists && info.Length == file.Size;
    }

    /// <summary>The wheels still to fetch plus the unpacked files must fit, so a full disk fails early and clearly.</summary>
    private static void CheckFreeSpace(IReadOnlyList<RuntimePackage> packages, string staging)
    {
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(staging))!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // can't tell (a network share, say): let the download try
        }
        var needed = packages.Sum(p =>
        {
            var partial = new FileInfo(Path.Combine(staging, p.FileName + PartialSuffix));
            var have = File.Exists(Path.Combine(staging, p.FileName)) ? p.Size : partial.Exists ? partial.Length : 0;
            return p.Size - have + p.Files.Sum(f => f.Size);
        });
        if (free < needed)
            throw new RuntimeDownloadException(
                $"The GPU libraries need {needed / 1e9:F1} GB of free space on {Path.GetPathRoot(staging)}; "
                + $"there is {free / 1e9:F1} GB.");
    }

    private static async Task DownloadWheelAsync(HttpClient http, RuntimePackage package, string wheel,
                                                 Action<long> progress, CancellationToken cancel)
    {
        var partial = wheel + PartialSuffix;
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > package.Size)
        {
            File.Delete(partial);
            have = 0;
        }
        if (have < package.Size)
            await FetchAsync(http, package.Url, partial, have, progress, cancel);
        await VerifyAsync(package, partial, cancel);
        File.Move(partial, wheel);
    }

    /// <summary>Append the rest of the file to <paramref name="partial"/>, which already holds its first <paramref name="have"/> bytes.</summary>
    private static async Task FetchAsync(HttpClient http, Uri url, string partial, long have, Action<long> progress,
                                         CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        // a server that ignores the range sends the whole file
        var resumed = response.StatusCode == HttpStatusCode.PartialContent;
        var written = resumed ? have : 0;
        await using var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
                                                FileShare.None, BufferSize, useAsync: true);
        await using var input = await response.Content.ReadAsStreamAsync(cancel);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var buffer = new byte[BufferSize];
        var reported = written;
        try
        {
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = await input.ReadAsync(buffer, stall.Token);
                if (read == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                written += read;
                if (written - reported >= ReportEveryBytes)
                {
                    progress(written);
                    reported = written;
                }
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new RuntimeDownloadException(
                $"The download stopped receiving data for {StallTimeout.TotalSeconds:F0} s. Try again; it continues where it stopped.");
        }
        progress(written);
    }

    private static async Task VerifyAsync(RuntimePackage package, string partial, CancellationToken cancel)
    {
        if (new FileInfo(partial).Length == package.Size)
        {
            await using var stream = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel)) == package.Sha256)
                return;
        }
        File.Delete(partial);
        throw new RuntimeDownloadException($"{package.FileName} did not download correctly (its size or checksum is wrong). "
                                           + "Try again to download it afresh.");
    }

    /// <summary>Unpack the package's files, checking each one, into a temporary name first so a half-written DLL is never loaded.</summary>
    private static async Task ExtractAsync(RuntimePackage package, string wheel, string folder, CancellationToken cancel)
    {
        using var archive = ZipFile.OpenRead(wheel);
        foreach (var file in package.Files)
        {
            var entry = archive.GetEntry(file.Entry)
                        ?? throw new RuntimeDownloadException($"{package.FileName} has no {file.Entry}.");
            var target = Path.Combine(folder, file.Name);
            var partial = target + PartialSuffix;
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var length = 0L;
                await using (var input = entry.Open())
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                                                         BufferSize, useAsync: true))
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                        length += read;
                    }
                }
                if (length != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                    throw new RuntimeDownloadException($"{file.Name} in {package.FileName} is not the expected file.");
                File.Move(partial, target, overwrite: true);
            }
            finally
            {
                File.Delete(partial);
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(Product.Name, typeof(RuntimeDownload).Assembly.GetName().Version?.ToString(3)));
        return client;
    }
}
