namespace Hushcue.Core.Subtitles;

/// <summary>One word as the engine heard it, with its leading space (" Hello"), in seconds.</summary>
public sealed record Word(string Text, double Start, double End);

/// <summary>One subtitle: shown from Start to End seconds.</summary>
public sealed record Cue(double Start, double End, string Text);
