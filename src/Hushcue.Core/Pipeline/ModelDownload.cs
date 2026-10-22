using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Hushcue.Core.Pipeline;

public sealed class ModelDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads a model into Hushcue's models folder: file by file into a staging folder beside it, continuing a file
/// where an earlier attempt stopped, checking each file's size and SHA-256, and moving the folder into place only once
/// every file is complete. A cancelled or failed download resumes on the next call.
/// </summary>
public static class ModelDownload
{
    private const int BufferSize = 1 << 20;
    private const long ReportEveryBytes = 8 << 20;
    private const string StagingSuffix = ".download";
    private const string PartialSuffix = ".part";
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);
    private static readonly Uri HuggingFace = new("https://huggingface.co/");
    private static readonly HttpClient Http = CreateClient();

    /// <param name="progress">bytes downloaded so far, and the model's total</param>
    /// <returns>the model's folder</returns>
    public static Task<string> DownloadAsync(string name, Action<long, long> progress, CancellationToken cancel = default) =>
        Models.Sources.TryGetValue(name, out var source)
            ? DownloadAsync(source, Path.Combine(Models.Folder, name), HuggingFace, progress, cancel)
            : throw new ModelDownloadException($"There is no model called '{name}'.");

    /// <summary>Download <paramref name="source"/> from <paramref name="server"/> (Hugging Face, or a test server) into
    /// <paramref name="target"/>.</summary>
    /// <param name="stallTimeout">give up when no data arrives for this long (default 30 s)</param>
    internal static async Task<string> DownloadAsync(ModelSource source, string target, Uri server, Action<long, long> progress,
                                                     CancellationToken cancel, TimeSpan? stallTimeout = null)
    {
        var name = Path.GetFileName(target);
        if (Directory.Exists(target))
            throw new ModelDownloadException($"{target} exists but does not hold a complete model. Remove it and try again.");
        var staging = target + StagingSuffix;
        try
        {
            Directory.CreateDirectory(staging);
            var done = 0L;
            foreach (var file in source.Files)
            {
                var path = Path.Combine(staging, file.Name);
                if (!File.Exists(path))
                    await DownloadFileAsync(new Uri(server, $"{source.Repository}/resolve/{source.Revision}/{file.Name}"), file,
                                            path, written => progress(done + written, source.Size),
                                            stallTimeout ?? DefaultStallTimeout, cancel);
                done += file.Size;
                progress(done, source.Size);
            }
            Directory.Move(staging, target);
            return target;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                   // HttpClient's own timeout, not the caller cancelling
                                   or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new ModelDownloadException(
                $"Could not download the {name} model: {e.Message} Try again; the download continues where it stopped.", e);
        }
    }

    private static async Task DownloadFileAsync(Uri url, ModelFile file, string path, Action<long> progress,
                                                TimeSpan stallTimeout, CancellationToken cancel)
    {
        var partial = path + PartialSuffix;
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > file.Size)
        {
            File.Delete(partial);
            have = 0;
        }
        if (have < file.Size)
            await FetchAsync(url, partial, have, progress, stallTimeout, cancel);
        await VerifyAsync(file, partial, cancel);
        File.Move(partial, path);
    }

    /// <summary>Append the rest of the file to <paramref name="partial"/>, which already holds its first <paramref name="have"/> bytes.</summary>
    private static async Task FetchAsync(Uri url, string partial, long have, Action<long> progress, TimeSpan stallTimeout,
                                         CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
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
                stall.CancelAfter(stallTimeout);
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
            throw new ModelDownloadException(
                $"The download stopped receiving data for {stallTimeout.TotalSeconds:F0} s. Try again; it continues where it stopped.");
        }
        progress(written);
    }

    private static async Task VerifyAsync(ModelFile file, string partial, CancellationToken cancel)
    {
        if (new FileInfo(partial).Length == file.Size)
        {
            await using var stream = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel)) == file.Sha256)
                return;
        }
        File.Delete(partial);
        throw new ModelDownloadException($"{file.Name} did not download correctly (its size or checksum is wrong). "
                                         + "Try again to download it afresh.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(Product.Name, typeof(ModelDownload).Assembly.GetName().Version?.ToString(3)));
        return client;
    }
}
