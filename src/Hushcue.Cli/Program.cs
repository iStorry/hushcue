using Hushcue.Core;

Console.WriteLine($"{Product.Name} {typeof(Product).Assembly.GetName().Version?.ToString(3)}");
Console.WriteLine(Product.Tagline);
return 0;
