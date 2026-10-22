using Hushcue.Cli;
using Hushcue.Core;

if (args.Length > 0 && args[0] == "transcribe")
    return await TranscribeCommand.RunAsync(args[1..]);
if (args.Length > 0 && args[0] == "job")
    return await JobCommand.RunAsync(args[1..]);
if (args.Length == 4 && args[0] == "features")
{
    // features MEDIA N_MELS OUT: raw float32 log-mel features (n_mels rows), for comparing with faster-whisper's
    var (data, frames) = new Hushcue.Core.Engine.MelSpectrogram(int.Parse(args[2])).Compute(await Hushcue.Core.Media.Audio.ReadAsync(args[1]));
    await File.WriteAllBytesAsync(args[3], System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()).ToArray());
    Console.WriteLine($"{frames} frames");
    return 0;
}

Console.WriteLine($"{Product.Name} {typeof(Product).Assembly.GetName().Version?.ToString(3)}");
Console.WriteLine(Product.Tagline);
Console.WriteLine();
Console.WriteLine(TranscribeCommand.Usage);
Console.WriteLine(JobCommand.Usage);
return 0;
