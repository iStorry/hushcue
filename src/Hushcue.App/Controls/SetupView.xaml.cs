using System.Diagnostics;
using Hushcue.Core.Pipeline;
using Hushcue.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Controls;

/// <summary>What the title bar badge shows: the GPU found, and whether the engine can use it.</summary>
public sealed record GpuStatus(string? Name, bool Cuda);

/// <summary>
/// First-run setup (and the system check behind the title bar badge): looks for an NVIDIA GPU with CUDA, ffmpeg with
/// NVENC, and the speech model, downloading the model with progress; a stopped download resumes. Without a GPU it
/// offers to continue on the CPU.
/// </summary>
public sealed partial class SetupView : UserControl
{
    // ModelDownload stages a model in models\NAME.download until every file is complete
    private const string StagingSuffix = ".download";

    private CancellationTokenSource? _download;
    private CancellationTokenSource? _runtimeDownload;
    private bool _runtimeReady;
    private bool _ffmpeg;
    private bool _engine = true;
    private bool _model;
    private int _cudaDevices;
    private long _rateBytes;
    private long _rateTime;
    private double _bytesPerSecond;

    public SetupView() => InitializeComponent();

    /// <summary>The user is done here (everything needed is in place).</summary>
    public event EventHandler? Continued;

    /// <summary>The GPU check finished: what the title bar badge should show.</summary>
    public event EventHandler<GpuStatus>? GpuChecked;

    public bool IsDownloading => _download is not null || _runtimeDownload is not null;

    /// <summary>ffmpeg, the model and the engine are all in place (as far as checked so far).</summary>
    public bool IsReadyToRun => _ffmpeg && _model && _engine;

    /// <summary>Whether the app can't work yet: no ffmpeg or no speech model (or the screen is forced on).</summary>
    public static bool IsNeeded() => SystemCheck.ForceSetup || !SystemCheck.HasFfmpeg() || SystemCheck.FindDefaultModel() is null;

    /// <param name="firstRun">"Set up Hushcue" at the first start; "System check" when opened from the badge</param>
    public void SetFirstRun(bool firstRun)
    {
        Heading.Text = firstRun ? $"Set up {Core.Product.Name}" : "System check";
        ContinueButton.Content = firstRun ? "Continue" : "Done";
    }

    /// <summary>Run every check, updating the rows as each one finishes.</summary>
    public async Task CheckAsync()
    {
        RecheckButton.IsEnabled = false;
        RefreshPath();
        GpuRow.Set(CheckState.Checking, "Looking for an NVIDIA GPU...");
        CheckModel();
        var gpu = Task.Run(() => (SystemCheck.NvidiaGpuName(), SystemCheck.Cuda()));
        _ffmpeg = SystemCheck.HasFfmpeg();
        var nvenc = _ffmpeg ? Task.Run(SafeHasNvenc) : null;
        if (_ffmpeg)
            FfmpegRow.Set(CheckState.Checking, "ffmpeg found", "Checking for the NVIDIA video encoder (NVENC)...");
        else
            FfmpegRow.Set(CheckState.Error, "ffmpeg not found",
                "Hushcue uses ffmpeg to read and write video. Install it with  winget install Gyan.FFmpeg  then select Check again.");
        UpdateButtons();

        var (name, (devices, error)) = await gpu;
        _cudaDevices = devices;
        _engine = error is null;
        ShowGpu(name, devices, error);
        await CheckGpuRuntimeAsync();
        GpuChecked?.Invoke(this, new GpuStatus(name, devices > 0 && _runtimeReady));

        if (nvenc is not null)
        {
            var hasNvenc = await nvenc;
            FfmpegRow.Set(CheckState.Ok, hasNvenc ? "ffmpeg found · NVENC available" : "ffmpeg found",
                hasNvenc ? "Burning subtitles into the picture uses the GPU's video encoder."
                         : "Burning subtitles into the picture uses the CPU encoder (x264), which is slower.");
        }
        RecheckButton.IsEnabled = true;
        UpdateButtons();
    }

    /// <summary>Stop a download (the window is closing); it resumes next time.</summary>
    public void Stop()
    {
        _download?.Cancel();
        _runtimeDownload?.Cancel();
    }

    private static bool SafeHasNvenc()
    {
        try
        {
            return SystemCheck.HasNvenc();
        }
        catch (Core.Media.MediaException)
        {
            return false;
        }
    }

    private void ShowGpu(string? name, int devices, string? error)
    {
        if (error is not null)
            GpuRow.Set(CheckState.Error, "The speech engine could not start", error + " Reinstalling Hushcue should fix this.");
        else if (devices > 0)
            GpuRow.Set(CheckState.Ok, $"{name ?? "NVIDIA GPU"} found · CUDA ready", "Transcribing runs on the GPU.");
        else if (name is not null)
            GpuRow.Set(CheckState.Warning, $"{name} found, but CUDA is not available",
                "Hushcue will transcribe on the CPU, which is much slower. Updating the NVIDIA driver may help.");
        else
            GpuRow.Set(CheckState.Warning, "No NVIDIA GPU found",
                "Hushcue will transcribe on the CPU. It works, but is much slower than on a GPU.");
    }

    /// <summary>
    /// The GPU runtime step (cuBLAS and NVRTC, which CTranslate2 needs on the GPU; not shipped with the app): only with
    /// a CUDA device. Without the libraries Hushcue still works, on the CPU.
    /// </summary>
    private async Task CheckGpuRuntimeAsync()
    {
        if (_cudaDevices == 0 || !_engine)
        {
            _runtimeReady = false;
            GpuRuntimeRow.Visibility = RuntimePanel.Visibility = RuntimeButton.Visibility = Visibility.Collapsed;
            return;
        }
        if (_runtimeDownload is not null)
            return;
        GpuRuntimeRow.Visibility = Visibility.Visible;
        var status = await Task.Run(RuntimeDownload.Check);
        _runtimeReady = status.IsReady;
        if (status.IsReady)
        {
            GpuRuntimeRow.Set(CheckState.Ok, "GPU libraries ready", "cuBLAS and NVRTC from NVIDIA, for transcribing on the GPU.");
            RuntimeButton.Visibility = Visibility.Collapsed;
            return;
        }
        GpuRuntimeRow.Set(CheckState.Warning, "GPU libraries needed",
            $"cuBLAS and NVRTC from NVIDIA: a one-time {Size(status.DownloadSize)} download. Until then Hushcue transcribes on the CPU.");
        RuntimeButton.Content = $"Download ({Size(status.DownloadSize)})";
        RuntimeButton.Visibility = Visibility.Visible;
    }

    private async void OnRuntimeDownload(object sender, RoutedEventArgs e)
    {
        if (_runtimeDownload is not null)
        {
            _runtimeDownload.Cancel();
            return;
        }
        _runtimeDownload = new CancellationTokenSource();
        RuntimeButton.Content = "Cancel";
        GpuRuntimeRow.Set(CheckState.Checking, "Downloading the GPU libraries", "cuBLAS and NVRTC, checked against their published checksums");
        RuntimeBar.Value = 0;
        RuntimeText.Text = "";
        RuntimePanel.Visibility = Visibility.Visible;
        UpdateButtons();
        var ui = DispatcherQueue;
        var token = _runtimeDownload.Token;
        string? failure = null;
        try
        {
            await Task.Run(() => RuntimeDownload.DownloadAsync((done, total) => ui.TryEnqueue(() =>
            {
                RuntimeBar.Value = total > 0 ? done * 1000.0 / total : 0;
                RuntimeText.Text = done < total ? $"{Size(done)} of {Size(total)}" : "Unpacking...";
            }), token), token);
        }
        catch (OperationCanceledException)
        {
            failure = "The download was cancelled. It continues where it stopped next time.";
        }
        catch (RuntimeDownloadException error)
        {
            failure = error.Message;
        }
        finally
        {
            _runtimeDownload.Dispose();
            _runtimeDownload = null;
            RuntimePanel.Visibility = Visibility.Collapsed;
        }
        if (failure is not null)
        {
            GpuRuntimeRow.Set(CheckState.Error, "The GPU libraries were not downloaded", failure);
            RuntimeButton.Content = "Try again";
            UpdateButtons();
            return;
        }
        await CheckAsync(); // the GPU row and the title bar badge pick the GPU up
    }
    private void CheckModel()
    {
        if (_download is not null)
            return;
        var name = SystemCheck.DefaultModel;
        var total = Models.Sources[name].Size;
        if (_model || SystemCheck.FindDefaultModel() is not null)
        {
            _model = true;
            ModelRow.Set(CheckState.Ok, "Speech model ready", $"{name}, saved on this PC");
            DownloadButton.Visibility = Visibility.Collapsed;
            return;
        }
        var have = StagedBytes(name);
        ModelRow.Set(CheckState.Warning, "Speech model needed",
            have > 0 ? $"{name}: {Size(have)} of {Size(total)} downloaded. The rest continues where it stopped."
                     : $"{name}: a one-time {Size(total)} download from Hugging Face.");
        DownloadButton.Content = have > 0 ? "Resume download" : $"Download ({Size(total)})";
        DownloadButton.Visibility = Visibility.Visible;
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_download is not null)
        {
            _download.Cancel(); // "Pause": the next download continues from here
            return;
        }
        var name = SystemCheck.DefaultModel;
        _download = new CancellationTokenSource();
        (_rateBytes, _rateTime, _bytesPerSecond) = (-1, 0, 0);
        DownloadButton.Content = "Pause";
        DownloadButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
        ModelRow.Set(CheckState.Checking, "Downloading the speech model", $"{name}, checked against its published checksum");
        DownloadPanel.Visibility = Visibility.Visible;
        UpdateButtons();
        var ui = DispatcherQueue;
        var token = _download.Token;
        try
        {
            await Task.Run(() => ModelDownload.DownloadAsync(name, (done, total) => ui.TryEnqueue(() => ShowDownload(done, total)), token),
                           token);
            _model = true;
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            ModelRow.Set(CheckState.Warning, "Download paused",
                $"{name}: {Size(StagedBytes(name))} of {Size(Models.Sources[name].Size)} downloaded. Resume any time, even after closing Hushcue.");
        }
        catch (ModelDownloadException error)
        {
            ModelRow.Set(CheckState.Error, "The download stopped", error.Message);
        }
        finally
        {
            _download.Dispose();
            _download = null;
            DownloadButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }
        if (_model)
        {
            CheckModel();
        }
        else
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
            DownloadButton.Content = "Resume download";
        }
        UpdateButtons();
    }

    private void ShowDownload(long done, long total)
    {
        if (_download is null)
            return;
        DownloadBar.Value = total > 0 ? done * 1000.0 / total : 0;
        var now = Stopwatch.GetTimestamp();
        if (_rateBytes < 0)
        {
            (_rateBytes, _rateTime) = (done, now);
        }
        else if (Stopwatch.GetElapsedTime(_rateTime, now).TotalSeconds is var seconds and >= 1)
        {
            var rate = (done - _rateBytes) / seconds;
            _bytesPerSecond = _bytesPerSecond == 0 ? rate : 0.7 * _bytesPerSecond + 0.3 * rate;
            (_rateBytes, _rateTime) = (done, now);
        }
        DownloadText.Text = _bytesPerSecond > 0
            ? $"{Size(done)} of {Size(total)} · {_bytesPerSecond / 1e6:F0} MB/s"
            : $"{Size(done)} of {Size(total)}";
    }

    private void UpdateButtons()
    {
        ContinueButton.IsEnabled = _ffmpeg && _model && _engine && _download is null && _runtimeDownload is null;
        if (Heading.Text != "System check")
            ContinueButton.Content = (_cudaDevices > 0 && _runtimeReady) || !_engine ? "Continue" : "Continue on CPU";
    }

    private async void OnRecheck(object sender, RoutedEventArgs e) => await CheckAsync();

    private void OnContinue(object sender, RoutedEventArgs e) => Continued?.Invoke(this, EventArgs.Empty);


    /// <summary>Bytes already downloaded into the staging folder: complete files and the one cut short.</summary>
    private static long StagedBytes(string name)
    {
        var staging = Path.Combine(Models.Folder, name + StagingSuffix);
        try
        {
            return Directory.Exists(staging) ? Directory.EnumerateFiles(staging).Sum(f => new FileInfo(f).Length) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Size(long bytes) => bytes < 1_000_000_000 ? $"{bytes / 1e6:F0} MB" : $"{bytes / 1e9:F1} GB";

    /// <summary>
    /// After installing ffmpeg (e.g. with winget) the new PATH is only in the registry: add its folders to this
    /// process's PATH so "Check again" finds it without restarting.
    /// </summary>
    private static void RefreshPath()
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var known = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User }
            .SelectMany(target => (Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(known.Add)
            .ToList();
        if (added.Count > 0)
            Environment.SetEnvironmentVariable("PATH", string.Join(';', [current, .. added]));
    }
}
