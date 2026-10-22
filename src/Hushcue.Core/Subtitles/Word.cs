namespace Hushcue.Core.Subtitles;

/// <summary>One word as the engine heard it, with its leading space (" Hello"), in seconds.</summary>
/// <param name="Probability">how sure Whisper was of it, 0..1 (1 when not known)</param>
public sealed record Word(string Text, double Start, double End, double Probability = 1);

/// <summary>One subtitle: shown from Start to End seconds.</summary>
public sealed record Cue(double Start, double End, string Text);
