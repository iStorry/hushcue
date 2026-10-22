using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Hushcue.Core.Pipeline;

namespace Hushcue.Core.Tests;

/// <summary>
/// The model download against a local fake of Hugging Face (HttpListener): progress, resume with a range request,
/// checksums, cancelling, stalls and errors. Never touches the real models.
/// </summary>
public sealed class ModelDownloadTests : IDisposable
{
    private const string Repository = "test-org/test-model";
    private const string Revision = "0123456789abcdef";
    private readonly string _dir = Directory.CreateTempSubdirectory("hushcue_download_").FullName;
    private readonly FakeHub _hub = new();
    private readonly ModelSource _source;

    public ModelDownloadTests()
    {
        var random = new Random(7);
        var files = new List<ModelFile>();
        foreach (var (name, size) in new[] { ("config.json", 300), ("tokenizer.json", 5000), ("model.bin", 300_000) })
        {
            var data = new byte[size];
            random.NextBytes(data);
            _hub.Files[$"/{Repository}/resolve/{Revision}/{name}"] = data;
            files.Add(new ModelFile(name, size, Convert.ToHexStringLower(SHA256.HashData(data))));
        }
        _source = new ModelSource(Repository, Revision, Repository, files);
    }

    public void Dispose()
    {
        _hub.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Target => Path.Combine(_dir, "models", "test-model");
    private string Staging => Target + ".download";
    private byte[] Data(string name) => _hub.Files[$"/{Repository}/resolve/{Revision}/{name}"];

    private Task<string> DownloadAsync(List<(long Done, long Total)>? progress = null, CancellationToken cancel = default,
                                       TimeSpan? stallTimeout = null) =>
        ModelDownload.DownloadAsync(_source, Target, _hub.Url, (done, total) => progress?.Add((done, total)), cancel, stallTimeout);

    private void AssertComplete()
    {
        foreach (var file in _source.Files)
            Assert.Equal(Data(file.Name), File.ReadAllBytes(Path.Combine(Target, file.Name)));
        Assert.Equal(_source.Files.Count, Directory.GetFiles(Target).Length);
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public async Task DownloadsCheckedFilesAndMovesTheFolderIntoPlace()
    {
        var progress = new List<(long Done, long Total)>();
        Assert.Equal(Target, await DownloadAsync(progress));

        AssertComplete();
        Assert.All(progress, p => Assert.Equal(_source.Size, p.Total));
        Assert.Equal(_source.Size, progress[^1].Done);
        Assert.All(progress.Zip(progress.Skip(1)), p => Assert.True(p.First.Done <= p.Second.Done));
        Assert.All(_hub.Requests, r => Assert.Null(r.Range));
        // the real server answers with a redirect to its file store, which is followed
        Assert.Equal(3, _hub.Requests.Count(r => r.Path.StartsWith("/store/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ResumesAnInterruptedFileWithARangeRequest()
    {
        _hub.DropAfter["model.bin"] = 100_000; // the connection breaks a third of the way into model.bin
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => DownloadAsync());
        Assert.Contains("continues where it stopped", error.Message);
        Assert.False(Directory.Exists(Target));
        Assert.True(File.Exists(Path.Combine(Staging, "config.json"))); // finished files are kept
        var partial = new FileInfo(Path.Combine(Staging, "model.bin.part"));
        Assert.True(partial.Length is > 0 and < 300_000, $"{partial.Length} bytes kept");

        _hub.Requests.Clear();
        var progress = new List<(long Done, long Total)>();
        await DownloadAsync(progress);

        AssertComplete();
        var request = Assert.Single(_hub.Requests, r => r.Path.StartsWith("/store/", StringComparison.Ordinal));
        Assert.EndsWith("/model.bin", request.Path, StringComparison.Ordinal);
        Assert.Equal($"bytes={partial.Length}-", request.Range); // kept through the redirect
        Assert.DoesNotContain(_hub.Requests, r => r.Path.EndsWith("/config.json", StringComparison.Ordinal));
        Assert.Equal([300L, 5300L, _source.Size], progress.Select(p => p.Done).Distinct()); // finished files count at once
    }

    [Fact]
    public async Task StartsOverWhenTheServerIgnoresTheRange()
    {
        _hub.DropAfter["model.bin"] = 100_000;
        await Assert.ThrowsAsync<ModelDownloadException>(() => DownloadAsync());
        _hub.IgnoreRange = true;
        await DownloadAsync();
        AssertComplete();
    }

    [Fact]
    public async Task ChecksumFailureDeletesTheBadFileAndAFreshTryWorks()
    {
        var good = Data("tokenizer.json");
        var bad = (byte[])good.Clone();
        bad[123] ^= 0xFF; // same size, different bytes
        _hub.Files[$"/{Repository}/resolve/{Revision}/tokenizer.json"] = bad;

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => DownloadAsync());
        Assert.Contains("tokenizer.json did not download correctly", error.Message);
        Assert.False(File.Exists(Path.Combine(Staging, "tokenizer.json.part")));
        Assert.False(File.Exists(Path.Combine(Staging, "tokenizer.json")));
        Assert.False(Directory.Exists(Target));

        _hub.Files[$"/{Repository}/resolve/{Revision}/tokenizer.json"] = good;
        await DownloadAsync();
        AssertComplete();
    }

    [Fact]
    public async Task ATooLongFileFailsItsCheck()
    {
        _hub.Files[$"/{Repository}/resolve/{Revision}/config.json"] = [.. Data("config.json"), 1, 2, 3];
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => DownloadAsync());
        Assert.Contains("config.json", error.Message);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task CancellingKeepsThePartialFileForNextTime()
    {
        _hub.StallAfter["model.bin"] = 100_000;
        using var cancel = new CancellationTokenSource();
        var download = DownloadAsync([], cancel.Token);
        await _hub.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(200);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.True(new FileInfo(Path.Combine(Staging, "model.bin.part")).Length > 0);

        _hub.StallAfter.Clear();
        await DownloadAsync();
        AssertComplete();
    }

    [Fact]
    public async Task AStalledDownloadGivesUpWithAMessage()
    {
        _hub.StallAfter["model.bin"] = 100_000;
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => DownloadAsync(stallTimeout: TimeSpan.FromSeconds(1)));
        Assert.Contains("stopped receiving data", error.Message);
    }

    [Fact]
    public async Task AServerErrorIsReported()
    {
        _hub.Files.TryRemove($"/{Repository}/resolve/{Revision}/model.bin", out _);
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => DownloadAsync());
        Assert.Contains("Could not download the test-model model", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task UnknownModelsAndLeftoverFoldersAreReported()
    {
        await Assert.ThrowsAsync<ModelDownloadException>(() => ModelDownload.DownloadAsync("no-such-model", (_, _) => { }));
        Directory.CreateDirectory(Target);
        var error = await Assert.ThrowsAsync<ModelDownloadException>(() => DownloadAsync());
        Assert.Contains("does not hold a complete model", error.Message);
    }

    /// <summary>
    /// Serves files like Hugging Face: /REPO/resolve/REVISION/NAME redirects to /store/..., which honours byte ranges.
    /// Can break the connection or stop sending part of the way into a file.
    /// </summary>
    private sealed class FakeHub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public sealed record Request(string Path, string? Range);

        public ConcurrentDictionary<string, byte[]> Files { get; } = new();
        public ConcurrentQueue<Request> Requests { get; } = new();
        /// <summary>File name to the byte after which the first download of it breaks off.</summary>
        public ConcurrentDictionary<string, int> DropAfter { get; } = new();
        /// <summary>File name to the byte after which every download of it stops sending (but stays open).</summary>
        public ConcurrentDictionary<string, int> StallAfter { get; } = new();
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreRange { get; set; }
        public Uri Url { get; }

        public FakeHub()
        {
            var port = FreePort();
            Url = new Uri($"http://localhost:{port}/");
            _listener.Prefixes.Add(Url.ToString());
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        private static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
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
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            var range = context.Request.Headers["Range"];
            Requests.Enqueue(new Request(path, range));
            var response = context.Response;
            try
            {
                if (!path.StartsWith("/store/", StringComparison.Ordinal))
                {
                    response.StatusCode = 302;
                    response.RedirectLocation = "/store" + path;
                    response.Close();
                    return;
                }
                if (!Files.TryGetValue(path["/store".Length..], out var data))
                {
                    response.StatusCode = 404;
                    response.Close();
                    return;
                }
                var start = 0;
                if (range is not null && !IgnoreRange && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    start = int.Parse(range[6..].TrimEnd('-'), System.Globalization.CultureInfo.InvariantCulture);
                    response.StatusCode = 206;
                    response.Headers["Content-Range"] = $"bytes {start}-{data.Length - 1}/{data.Length}";
                }
                response.ContentLength64 = data.Length - start;
                var name = path[(path.LastIndexOf('/') + 1)..];
                var body = response.OutputStream;
                if (DropAfter.TryRemove(name, out var drop) && drop > start)
                {
                    await body.WriteAsync(data.AsMemory(start, drop - start));
                    await body.FlushAsync();
                    response.Abort(); // the connection breaks
                    return;
                }
                if (StallAfter.TryGetValue(name, out var stall) && stall > start)
                {
                    await body.WriteAsync(data.AsMemory(start, stall - start));
                    await body.FlushAsync();
                    Stalled.TrySetResult();
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
                await body.WriteAsync(data.AsMemory(start));
                response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                response.Abort();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _stop.Dispose();
        }
    }
}
