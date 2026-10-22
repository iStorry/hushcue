using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hushcue.Core.Updates;

/// <summary>A file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>A published Hushcue release, as GitHub's "latest release" reports it.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, Uri Page, ReleaseAsset? Installer,
                                 ReleaseAsset? Checksums);

/// <summary>Something went wrong checking for or downloading an update; the message is for the user.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Updates from the project's GitHub releases: check the latest release, download its installer (checked against the
/// release's SHA256SUMS.txt) and run it silently, which closes Hushcue, installs over it and starts it again.
/// A portable copy (no uninstaller next to it) is pointed at the release page instead.
/// </summary>
public static class Updates
{
    public const string Repository = "iStorry/hushcue";

    public static Uri ReleasesPage => new($"https://github.com/{Repository}/releases");

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The running version (major.minor.patch).</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(Product).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>Folder the installers are downloaded to (%LOCALAPPDATA%\Hushcue\updates).</summary>
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hushcue", "updates");

    /// <summary>The latest release if it is newer than <see cref="Current"/>, else null.</summary>
    /// <exception cref="UpdateException">GitHub could not be reached or answered with something unexpected.</exception>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken cancel = default)
    {
        string json;
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancel);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // no published release yet
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new UpdateException("Could not reach GitHub to check for updates.", e);
        }
        var release = Parse(json);
        return release is not null && release.Version > Current ? release : null;
    }

    /// <summary>A release from GitHub's JSON; null if it has no usable version tag.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (ParseVersion(tag) is not { } version)
                return null;
            var assets = root.TryGetProperty("assets", out var list)
                ? list.EnumerateArray().Select(a => new ReleaseAsset(a.GetProperty("name").GetString() ?? "",
                                                                     new Uri(a.GetProperty("browser_download_url").GetString()!),
                                                                     a.TryGetProperty("size", out var size) ? size.GetInt64() : 0)).ToList()
                : [];
            var page = root.TryGetProperty("html_url", out var url) && url.GetString() is { } html ? new Uri(html) : ReleasesPage;
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new ReleaseInfo(version, tag, notes, page,
                assets.FirstOrDefault(a => a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)),
                assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            throw new UpdateException("GitHub's answer about the latest release could not be read.", e);
        }
    }

    /// <summary>"v1.2.3" or "1.2.3" (a "-beta.1" suffix is ignored) as a version; null if it isn't one.</summary>
    public static Version? ParseVersion(string tag)
    {
        var text = tag.TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0)
            text = text[..dash];
        return Version.TryParse(text, out var v) && v.Major >= 0 ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>The SHA-256 SHA256SUMS.txt lists for <paramref name="fileName"/> ("hash  name" lines), lower case.</summary>
    public static string? ChecksumFor(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Download the release's installer into <see cref="Folder"/> and check it against SHA256SUMS.txt.
    /// </summary>
    /// <param name="progress">bytes so far and the total</param>
    /// <returns>the installer's path</returns>
    public static Task<string> DownloadInstallerAsync(ReleaseInfo release, Action<long, long> progress,
                                                      CancellationToken cancel = default) =>
        DownloadInstallerAsync(release, Folder, Http, progress, cancel);

    internal static async Task<string> DownloadInstallerAsync(ReleaseInfo release, string folder, HttpClient http,
                                                              Action<long, long> progress, CancellationToken cancel)
    {
        if (release.Installer is not { } installer || release.Checksums is not { } checksums)
            throw new UpdateException($"Release {release.Tag} has no installer to update with.");
        var path = Path.Combine(folder, installer.Name);
        var partial = path + ".part";
        try
        {
            Directory.CreateDirectory(folder);
            var expected = ChecksumFor(await http.GetStringAsync(checksums.Url, cancel), installer.Name)
                           ?? throw new UpdateException($"Release {release.Tag} does not list a checksum for {installer.Name}.");
            using (var response = await http.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? installer.Size;
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = File.Create(partial);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    progress(done, total);
                }
            }
            string actual;
            await using (var file = File.OpenRead(partial))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel));
            if (actual != expected)
                throw new UpdateException($"The downloaded {installer.Name} is damaged (its checksum does not match). Try again later.");
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                    || (e is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            throw new UpdateException("The update could not be downloaded: " + e.Message, e);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>Installed with the installer (its uninstaller sits next to the exe), so it can update itself.</summary>
    public static bool IsInstalled(string appFolder) => File.Exists(Path.Combine(appFolder, "unins000.exe"));

    /// <summary>
    /// Run a downloaded installer silently. It waits for Hushcue to close (the installer checks the app's mutex), installs
    /// over it in the same place, and starts it again (/RELAUNCH=1, see installer/hushcue.iss).
    /// </summary>
    public static void RunInstaller(string installer) =>
        Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Product.Name, Current.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
