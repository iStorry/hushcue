using Hushcue.Cli;
using Hushcue.Core;

if (args.Length > 0 && args[0] == "transcribe")
    return await TranscribeCommand.RunAsync(args[1..]);

Console.WriteLine($"{Product.Name} {typeof(Product).Assembly.GetName().Version?.ToString(3)}");
Console.WriteLine(Product.Tagline);
Console.WriteLine();
Console.WriteLine(TranscribeCommand.Usage);
return 0;
