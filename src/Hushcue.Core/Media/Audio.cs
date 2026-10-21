namespace Hushcue.Core.Media;

public static class Audio
{
    public const int SampleRate = 16000;

    /// <summary>Optional clean-up before transcription: cut rumble, reduce noise, even out loudness.</summary>
    public const string EnhanceFilter = "highpass=f=80,afftdn=nf=-25,dynaudnorm=f=150:g=15";

    /// <summary>
    /// The audio of any video or audio file as 16 kHz mono float samples, decoded by ffmpeg straight into memory
    /// (no temporary WAV).
    /// </summary>
    public static async Task<float[]> ReadAsync(string path, bool enhance = false, CancellationToken cancel = default)
    {
        List<string> cmd = [Tools.Ffmpeg, "-v", "error", "-nostdin", "-i", path, "-vn", "-ac", "1", "-ar", $"{SampleRate}"];
        if (enhance)
            cmd.AddRange(["-af", EnhanceFilter]);
        cmd.AddRange(["-f", "f32le", "pipe:1"]);

        using var process = System.Diagnostics.Process.Start(Tools.StartInfo(cmd))
                            ?? throw new MediaException("ffmpeg did not start");
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var pcm = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(pcm, cancel);
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            Tools.Kill(process);
            throw;
        }
        if (process.ExitCode != 0)
        {
            var tail = (await errors).Trim().Split('\n').TakeLast(3).Select(l => l.Trim());
            throw new MediaException("ffmpeg could not read audio from this file.\n" + string.Join("\n", tail));
        }
        var samples = new float[pcm.Length / sizeof(float)];
        Buffer.BlockCopy(pcm.GetBuffer(), 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }
}
