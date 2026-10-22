using Hushcue.Core.Engine;
using Hushcue.Core.Subtitles;

namespace Hushcue.Core.Tests;

/// <summary>
/// A Whisper stand-in, like the fake models of SubGen's tests: a decode of the whole file (audio of
/// <paramref name="wholeSamples"/> samples, or any audio when that is null) returns <paramref name="whole"/> as one
/// segment, a decode of anything shorter (a clip re-decoded by loop recovery) returns <paramref name="clip"/>.
/// </summary>
internal sealed class FakeTranscriber(IReadOnlyList<Word> whole, IReadOnlyList<Word>? clip = null, int? wholeSamples = null,
                                      double duration = 20) : ITranscriber
{
    public sealed record Call(bool Whole, float RepetitionPenalty, int Samples, TranscribeOptions Options);

    private readonly Lock _lock = new();

    public List<Call> Calls { get; } = [];

    /// <summary>Runs before each decode, e.g. to block or throw.</summary>
    public Action<Call>? OnTranscribe { get; init; }

    public (IEnumerable<Segment> Segments, TranscriptionInfo Info) Transcribe(float[] audio, TranscribeOptions options,
                                                                              CancellationToken cancel = default)
    {
        var call = new Call(wholeSamples is null || audio.Length == wholeSamples, options.RepetitionPenalty, audio.Length, options);
        lock (_lock)
            Calls.Add(call);
        OnTranscribe?.Invoke(call);
        var words = call.Whole ? whole : clip ?? [];
        IEnumerable<Segment> segments = words.Count == 0 ? [] : [Segment(words)];
        return (segments, new TranscriptionInfo("en", 1, duration, duration));
    }

    private static Segment Segment(IReadOnlyList<Word> words) =>
        new(0, 0, words[0].Start, words[^1].End, string.Join(" ", words.Select(w => w.Text)), [], 0, -0.3, 1, 0,
            [.. words.Select(w => new TimedWord(w.Text, w.Start, w.End, 0.9))]);
}
