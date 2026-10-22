namespace Hushcue.Core.Media;

public static class Audio
{
    public const int SampleRate = 16000;

    /// <summary>Optional clean-up before transcription: cut rumble, reduce noise, even out loudness.</summary>
    public const string EnhanceFilter = "highpass=f=80,afftdn=nf=-25,dynaudnorm=f=150:g=15";

    /// <summary>
    /// The audio of any video or audio file as 16 kHz mono float samples, decoded by ffmpeg straight into memory
    /// (no temporary WAV). Samples go through 16-bit first, exactly as faster-whisper's decoder does,
    /// so the model hears the same values.
    /// </summary>
    public static async Task<float[]> ReadAsync(string path, bool enhance = false, CancellationToken cancel = default)
    {
        List<string> cmd = [Tools.Ffmpeg, "-v", "error", "-nostdin", "-i", path, "-vn", "-ac", "1", "-ar", $"{SampleRate}"];
        if (enhance)
            cmd.AddRange(["-af", EnhanceFilter]);
        cmd.AddRange(["-f", "s16le", "pipe:1"]);

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
        var pcm16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.GetBuffer().AsSpan(0, (int)pcm.Length));
        var samples = new float[pcm16.Length];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = pcm16[i] / 32768.0f;
        return samples;
    }
}
