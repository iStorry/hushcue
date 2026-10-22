using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Hushcue.Core.Updates;

namespace Hushcue.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("hushcue_update_").FullName;

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    private const string ReleaseJson = """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://github.com/iStorry/hushcue/releases/tag/v0.2.0",
          "body": "Faster review page.",
          "assets": [
            { "name": "Hushcue-0.2.0-win-x64.zip", "browser_download_url": "https://example.com/z.zip", "size": 10 },
            { "name": "Hushcue-0.2.0-setup.exe", "browser_download_url": "https://example.com/s.exe", "size": 20 },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.com/SHA256SUMS.txt", "size": 30 }
          ]
        }
        """;

    [Fact]
    public void ReadsTheLatestRelease()
    {
        var release = Updates.Updates.Parse(ReleaseJson)!;
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal("Faster review page.", release.Notes);
        Assert.Equal("Hushcue-0.2.0-setup.exe", release.Installer!.Name);
        Assert.Equal("SHA256SUMS.txt", release.Checksums!.Name);
        Assert.Equal("https://github.com/iStorry/hushcue/releases/tag/v0.2.0", release.Page.ToString());
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    public void ReadsVersionTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), Updates.Updates.ParseVersion(tag));

    [Fact]
    public void IgnoresTagsThatAreNotVersions()
    {
        Assert.Null(Updates.Updates.ParseVersion("nightly"));
        Assert.Null(Updates.Updates.Parse("""{ "tag_name": "nightly", "assets": [] }"""));
    }

    [Fact]
    public void FindsAFilesChecksum()
    {
        const string sums = "ABC123  Hushcue-0.2.0-setup.exe\r\ndef456  Hushcue-0.2.0-win-x64.zip\r\n";
        Assert.Equal("abc123", Updates.Updates.ChecksumFor(sums, "Hushcue-0.2.0-setup.exe"));
        Assert.Equal("def456", Updates.Updates.ChecksumFor(sums, "hushcue-0.2.0-WIN-x64.zip"));
        Assert.Null(Updates.Updates.ChecksumFor(sums, "other.exe"));
    }

    [Fact]
    public void KnowsAnInstalledCopyFromAPortableOne()
    {
        Assert.False(Updates.Updates.IsInstalled(_tmp));
        File.WriteAllText(Path.Combine(_tmp, "unins000.exe"), "");
        Assert.True(Updates.Updates.IsInstalled(_tmp));
    }

    [Fact]
    public async Task DownloadsAndChecksTheInstaller()
    {
        var installer = Encoding.UTF8.GetBytes(new string('x', 200_000));
        using var server = new FakeServer(new()
        {
            ["/s.exe"] = installer,
            ["/SHA256SUMS.txt"] = Encoding.ASCII.GetBytes($"{Hash(installer)}  Hushcue-0.2.0-setup.exe\n"),
        });
        var reports = new List<(long, long)>();
        var path = await Updates.Updates.DownloadInstallerAsync(Release(server), _tmp, server.Client, (d, t) => reports.Add((d, t)), default);
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        Assert.Equal((installer.Length, installer.Length), reports[^1]);
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task RejectsADamagedInstaller()
    {
        using var server = new FakeServer(new()
        {
            ["/s.exe"] = Encoding.UTF8.GetBytes("tampered"),
            ["/SHA256SUMS.txt"] = Encoding.ASCII.GetBytes($"{Hash(Encoding.UTF8.GetBytes("original"))}  Hushcue-0.2.0-setup.exe\n"),
        });
        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            Updates.Updates.DownloadInstallerAsync(Release(server), _tmp, server.Client, (_, _) => { }, default));
        Assert.Contains("damaged", error.Message);
        Assert.Empty(Directory.GetFiles(_tmp));
    }

    [Fact]
    public async Task NeedsAChecksumForTheInstaller()
    {
        using var server = new FakeServer(new()
        {
            ["/s.exe"] = [1, 2, 3],
            ["/SHA256SUMS.txt"] = Encoding.ASCII.GetBytes("abc  something-else.exe\n"),
        });
        await Assert.ThrowsAsync<UpdateException>(() =>
            Updates.Updates.DownloadInstallerAsync(Release(server), _tmp, server.Client, (_, _) => { }, default));
    }

    private static ReleaseInfo Release(FakeServer server) =>
        new(new Version(0, 2, 0), "v0.2.0", "", new Uri(server.Prefix),
            new ReleaseAsset("Hushcue-0.2.0-setup.exe", new Uri(server.Prefix + "s.exe"), 0),
            new ReleaseAsset("SHA256SUMS.txt", new Uri(server.Prefix + "SHA256SUMS.txt"), 0));

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Serves fixed files on a free localhost port.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, byte[]> _files;

        public FakeServer(Dictionary<string, byte[]> files)
        {
            _files = files;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Prefix { get; }

        public HttpClient Client { get; } = new();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                if (_files.TryGetValue(context.Request.Url!.AbsolutePath, out var body))
                {
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Close();
            Client.Dispose();
        }
    }
}
