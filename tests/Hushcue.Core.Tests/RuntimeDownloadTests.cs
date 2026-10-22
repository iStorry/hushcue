using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Hushcue.Core.Pipeline;

namespace Hushcue.Core.Tests;

/// <summary>The GPU runtime download against a local HTTP server and small fake wheels (never the real ones).</summary>
public sealed class RuntimeDownloadTests : IDisposable
{
    /// <summary>Serves fixed files, honouring Range unless told not to, and can cut the first response short.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, byte[]> _files = new();
        private readonly Task _loop;

        public FakeServer()
        {
            var port = FreePort();
            BaseUrl = $"http://localhost:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }
        public List<(string Path, string? Range)> Requests { get; } = [];
        public bool IgnoreRange { get; set; }

        /// <summary>Send only this many bytes of the next response, then drop the connection.</summary>
        public long? CutNextAfter { get; set; }

        public Uri Add(string name, byte[] content)
        {
            _files["/" + name] = content;
            return new Uri(BaseUrl + name);
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                var path = context.Request.Url!.AbsolutePath;
                var range = context.Request.Headers["Range"];
                lock (Requests)
                    Requests.Add((path, range));
                var response = context.Response;
                try
                {
                    if (!_files.TryGetValue(path, out var content))
                    {
                        response.StatusCode = 404;
                        response.Close();
                        continue;
                    }
                    var start = 0L;
                    if (range is not null && !IgnoreRange)
                    {
                        start = long.Parse(range["bytes=".Length..].TrimEnd('-'));
                        response.StatusCode = 206;
                        response.Headers["Content-Range"] = $"bytes {start}-{content.Length - 1}/{content.Length}";
                    }
                    response.ContentLength64 = content.Length - start;
                    if (CutNextAfter is { } cut)
                    {
                        CutNextAfter = null;
                        await response.OutputStream.WriteAsync(content.AsMemory((int)start, (int)cut));
                        await response.OutputStream.FlushAsync();
                        await Task.Delay(200); // let the bytes reach the client before the connection drops
                        response.Abort();
                        continue;
                    }
                    await response.OutputStream.WriteAsync(content.AsMemory((int)start));
                    response.Close();
                }
                catch (Exception e) when (e is HttpListenerException or IOException)
                {
                    // the client went away
                }
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hushcue-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServer _server = new();
    private readonly HttpClient _http = new();

    private string Folder => Path.Combine(_root, "runtime");
    private string Staging => Path.Combine(_root, "runtime.download");

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>A fake wheel holding the given entries (as a real one, more than Hushcue takes out of it).</summary>
    private static byte[] Wheel(params (string Entry, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entry, content) in entries)
            {
                using var stream = zip.CreateEntry(entry, CompressionLevel.NoCompression).Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private RuntimePackage Package(string name, (string Entry, string Name, byte[] Content)[] taken, byte[]? extra = null,
                                   string? sha256 = null)
    {
        var entries = taken.Select(t => (t.Entry, t.Content)).ToList();
        if (extra is not null)
            entries.Add(($"{name}/bin/unused.dll", extra));
        var wheel = Wheel([.. entries]);
        var url = _server.Add($"{name}-1.0-py3-none-win_amd64.whl", wheel);
        return new RuntimePackage(name, "1.0", url, wheel.Length, sha256 ?? Sha256(wheel),
            [.. taken.Select(t => new RuntimeFile(t.Entry, t.Name, t.Content.Length, Sha256(t.Content)))]);
    }

    private (RuntimePackage A, RuntimePackage B, byte[] ADll, byte[] BDll) TwoPackages()
    {
        var aDll = RandomBytes(300_000, 1);
        var bDll = RandomBytes(120_000, 2);
        var a = Package("fake_cublas", [("fake_cublas/bin/a64_12.dll", "a64_12.dll", aDll)], extra: RandomBytes(5000, 3));
        var b = Package("fake_nvrtc", [("fake_nvrtc/bin/b64_12.dll", "b64_12.dll", bDll)]);
        return (a, b, aDll, bDll);
    }

    [Fact]
    public async Task DownloadsTheWheelsAndUnpacksOnlyTheFilesItNeeds()
    {
        var (a, b, aDll, bDll) = TwoPackages();
        Assert.Equal([a, b], RuntimeDownload.Check([a, b], [Folder]).Missing);
        var reports = new List<(long Done, long Total)>();

        await RuntimeDownload.DownloadAsync([a, b], Folder, _http, (done, total) => reports.Add((done, total)), default);

        Assert.Equal(aDll, File.ReadAllBytes(Path.Combine(Folder, "a64_12.dll")));
        Assert.Equal(bDll, File.ReadAllBytes(Path.Combine(Folder, "b64_12.dll")));
        Assert.Equal(["a64_12.dll", "b64_12.dll"], Directory.GetFiles(Folder).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(Staging));
        Assert.True(RuntimeDownload.Check([a, b], [Folder]).IsReady);
        Assert.Equal((a.Size + b.Size, a.Size + b.Size), reports[^1]);
        Assert.All(reports, r => Assert.InRange(r.Done, 0, r.Total));

        // nothing left to do: no more requests
        var requests = _server.Requests.Count;
        await RuntimeDownload.DownloadAsync([a, b], Folder, _http, (_, _) => { }, default);
        Assert.Equal(requests, _server.Requests.Count);
    }

    [Fact]
    public async Task ResumesAnInterruptedDownloadWithARangeRequest()
    {
        var (a, _, aDll, _) = TwoPackages();
        _server.CutNextAfter = 100_000;

        await Assert.ThrowsAsync<RuntimeDownloadException>(
            () => RuntimeDownload.DownloadAsync([a], Folder, _http, (_, _) => { }, default));
        var have = new FileInfo(Path.Combine(Staging, a.FileName + ".part")).Length;
        Assert.InRange(have, 1, a.Size - 1);

        await RuntimeDownload.DownloadAsync([a], Folder, _http, (_, _) => { }, default);

        Assert.Equal(aDll, File.ReadAllBytes(Path.Combine(Folder, "a64_12.dll")));
        Assert.Equal($"bytes={have}-", _server.Requests[^1].Range);
    }

    [Fact]
    public async Task StartsAgainWhenTheServerIgnoresTheRange()
    {
        var (a, _, aDll, _) = TwoPackages();
        Directory.CreateDirectory(Staging);
        File.WriteAllBytes(Path.Combine(Staging, a.FileName + ".part"), RandomBytes(50_000, 9));
        _server.IgnoreRange = true;

        await RuntimeDownload.DownloadAsync([a], Folder, _http, (_, _) => { }, default);

        Assert.Equal(aDll, File.ReadAllBytes(Path.Combine(Folder, "a64_12.dll")));
        Assert.Equal("bytes=50000-", _server.Requests.Single().Range);
    }

    [Fact]
    public async Task RejectsAWheelWithTheWrongChecksumAndDiscardsIt()
    {
        var a = Package("fake_cublas", [("fake_cublas/bin/a64_12.dll", "a64_12.dll", RandomBytes(1000, 4))],
                        sha256: new string('0', 64));

        var error = await Assert.ThrowsAsync<RuntimeDownloadException>(
            () => RuntimeDownload.DownloadAsync([a], Folder, _http, (_, _) => { }, default));

        Assert.Contains("checksum", error.Message);
        Assert.Empty(Directory.GetFiles(Staging));
        Assert.False(File.Exists(Path.Combine(Folder, "a64_12.dll")));
    }

    [Fact]
    public async Task RejectsAFileInsideTheWheelThatIsNotTheExpectedOne()
    {
        var a = Package("fake_cublas", [("fake_cublas/bin/a64_12.dll", "a64_12.dll", RandomBytes(1000, 4))]);
        a = a with { Files = [a.Files[0] with { Sha256 = new string('0', 64) }] };

        await Assert.ThrowsAsync<RuntimeDownloadException>(
            () => RuntimeDownload.DownloadAsync([a], Folder, _http, (_, _) => { }, default));

        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task CancellingKeepsWhatIsDownloadedForTheNextAttempt()
    {
        var (a, b, aDll, bDll) = TwoPackages();
        using var cancel = new CancellationTokenSource();

        // cancel once the first wheel is in, before it is checked and unpacked
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeDownload.DownloadAsync(
            [a, b], Folder, _http, (done, _) => { if (done >= a.Size) cancel.Cancel(); }, cancel.Token));
        Assert.Empty(Directory.GetFiles(Folder));

        await RuntimeDownload.DownloadAsync([a, b], Folder, _http, (_, _) => { }, default);

        Assert.Equal(aDll, File.ReadAllBytes(Path.Combine(Folder, "a64_12.dll")));
        Assert.Equal(bDll, File.ReadAllBytes(Path.Combine(Folder, "b64_12.dll")));
        Assert.Single(_server.Requests, r => r.Path.EndsWith(a.FileName));
    }

    [Fact]
    public void CheckLooksInEveryFolderAndComparesSizes()
    {
        var (a, b, aDll, _) = TwoPackages();
        var bundled = Path.Combine(_root, "app", "runtime");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(bundled, "a64_12.dll"), aDll);
        File.WriteAllBytes(Path.Combine(Folder, "b64_12.dll"), RandomBytes(10, 5)); // an older or broken copy

        var status = RuntimeDownload.Check([a, b], [bundled, Folder]);

        Assert.Equal([b], status.Missing);
        Assert.Equal(b.Size, status.DownloadSize);
        Assert.False(status.IsReady);
    }

    [Fact]
    public void FilesThatAreNotRequiredDoNotCountAsMissing()
    {
        var (a, _, aDll, _) = TwoPackages();
        a = a with { Files = [.. a.Files, new RuntimeFile("LICENSE.txt", "LICENSE.txt", 10, new string('0', 64), Required: false)] };
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "a64_12.dll"), aDll);

        Assert.True(RuntimeDownload.Check([a], [Folder]).IsReady);
    }

    [Fact]
    public void ThePinnedPackagesAreWellFormed()
    {
        foreach (var package in RuntimeDownload.GpuPackages)
        {
            Assert.Equal("files.pythonhosted.org", package.Url.Host);
            Assert.Equal($"{package.Name.Replace('-', '_')}-{package.Version}-py3-none-win_amd64.whl", package.FileName);
            Assert.Matches("^[0-9a-f]{64}$", package.Sha256);
            Assert.All(package.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        }
        var names = RuntimeDownload.GpuPackages.SelectMany(p => p.Files).Select(f => f.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("cublas64_12.dll", names);
        Assert.Contains("cublasLt64_12.dll", names);
    }
}
