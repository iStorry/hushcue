using System.Diagnostics;
using System.Text;

namespace Hushcue.Core.Media;

/// <summary>Something ffmpeg or ffprobe could not do; the message is meant for the user.</summary>
public sealed class MediaException(string message) : Exception(message);

/// <summary>Finding and running ffmpeg / ffprobe.</summary>
public static class Tools
{
    private static readonly string InstallCommand =
        OperatingSystem.IsWindows() ? "winget install Gyan.FFmpeg" : "brew install ffmpeg";

    public static string Ffmpeg => Find("ffmpeg", $"ffmpeg was not found. Install it with: {InstallCommand}");

    public static string Ffprobe => Find("ffprobe", "ffprobe was not found (it ships with ffmpeg).");

    /// <summary>A copy bundled next to the app (ffmpeg\NAME.exe) wins over one on PATH.</summary>
    private static string Find(string name, string error)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", file);
        if (File.Exists(bundled))
            return bundled;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;
            var candidate = Path.Combine(dir.Trim('"'), file);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new MediaException(error);
    }

    internal static ProcessStartInfo StartInfo(IReadOnlyList<string> command, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in command.Skip(1))
            info.ArgumentList.Add(arg);
        if (workingDirectory is not null)
            info.WorkingDirectory = workingDirectory;
        return info;
    }

    /// <summary>Run to completion; returns (exit code, stdout, stderr).</summary>
    internal static async Task<(int ExitCode, string Output, string Errors)> RunAsync(
        IReadOnlyList<string> command, CancellationToken cancel = default)
    {
        using var process = Process.Start(StartInfo(command)) ?? throw new MediaException($"{command[0]} did not start");
        var output = process.StandardOutput.ReadToEndAsync(cancel);
        var errors = process.StandardError.ReadToEndAsync(cancel);
        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        return (process.ExitCode, await output, await errors);
    }

    /// <summary>
    /// Run an ffmpeg command that writes <paramref name="output"/>, reporting progress from <c>-progress pipe:1</c>.
    /// Removes the output if ffmpeg fails or is cancelled.
    /// </summary>
    internal static async Task RunToFileAsync(IReadOnlyList<string> command, string? workingDirectory, double duration,
                                              string output, Action<double> onFraction, CancellationToken cancel)
    {
        using var process = Process.Start(StartInfo(command, workingDirectory))
                            ?? throw new MediaException("ffmpeg did not start");
        var errorTail = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (errorTail)
            {
                errorTail.Enqueue(e.Data.TrimEnd());
                if (errorTail.Count > 12)
                    errorTail.Dequeue();
            }
        };
        process.BeginErrorReadLine();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancel) is { } line)
            {
                if (ParseProgress(line, duration) is { } fraction)
                    onFraction(fraction);
            }
            await process.WaitForExitAsync(cancel);
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            DeleteWhenReleased(output);
            throw;
        }
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            DeleteWhenReleased(output);
            lock (errorTail)
                throw new MediaException("ffmpeg could not write the video.\n" + string.Join("\n", errorTail));
        }
        onFraction(1.0);
    }

    /// <summary>Fraction done from one line of <c>ffmpeg -progress</c> output, or null for other lines.</summary>
    public static double? ParseProgress(string line, double duration)
    {
        var parts = line.Trim().Split('=', 2);
        if (parts.Length < 2 || parts[0] is not ("out_time_us" or "out_time_ms"))
            return null; // (out_time_ms is in microseconds too - an old ffmpeg naming quirk)
        var digits = parts[1].TrimStart('-');
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || !long.TryParse(parts[1], out var micros))
            return null;
        return duration > 0 ? Math.Max(0.0, Math.Min(micros / 1_000_000.0 / duration, 1.0)) : null;
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    /// <summary>Windows keeps a killed process's files locked for a moment: retry briefly instead of failing.</summary>
    internal static void DeleteWhenReleased(string path)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
