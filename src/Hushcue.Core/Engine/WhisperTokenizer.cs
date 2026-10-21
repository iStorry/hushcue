using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hushcue.Core.Engine;

/// <summary>
/// Whisper's byte-level BPE tokenizer read from the model's tokenizer.json, plus the special tokens faster-whisper's
/// Tokenizer wrapper exposes. Decoding skips special tokens and turns invalid UTF-8 into U+FFFD, like the Hugging Face
/// tokenizers library.
/// </summary>
public sealed partial class WhisperTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly string[] _idToToken;
    private readonly HashSet<int> _special = [];
    private readonly Dictionary<(string, string), int> _mergeRanks = [];
    private readonly Dictionary<string, int> _added = [];
    private static readonly char[] ByteToChar = BuildByteToChar();
    private static readonly Dictionary<char, byte> CharToByte =
        Enumerable.Range(0, 256).ToDictionary(b => ByteToChar[b], b => (byte)b);

    public WhisperTokenizer(string tokenizerJsonPath, bool multilingual, string task = "transcribe", string? language = "en")
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(tokenizerJsonPath));
        var model = json.RootElement.GetProperty("model");
        _vocab = model.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        var rank = 0;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            var (left, right) = merge.ValueKind == JsonValueKind.Array
                ? (merge[0].GetString()!, merge[1].GetString()!)
                : SplitMerge(merge.GetString()!);
            _mergeRanks.TryAdd((left, right), rank++);
        }
        var maxId = _vocab.Values.Max();
        foreach (var added in json.RootElement.GetProperty("added_tokens").EnumerateArray())
        {
            var id = added.GetProperty("id").GetInt32();
            var content = added.GetProperty("content").GetString()!;
            _added[content] = id;
            if (added.GetProperty("special").GetBoolean())
                _special.Add(id);
            maxId = Math.Max(maxId, id);
        }
        _idToToken = new string[maxId + 1];
        foreach (var (token, id) in _vocab)
            _idToToken[id] = token;
        foreach (var (token, id) in _added)
            _idToToken[id] = token;

        Multilingual = multilingual;
        if (multilingual)
        {
            Task = TokenToId($"<|{task}|>") ?? throw new ArgumentException($"unknown task {task}");
            SetLanguage(language ?? "en");
        }
        else
        {
            LanguageCode = "en";
        }
    }

    public bool Multilingual { get; }
    public int? Task { get; }
    public int? Language { get; private set; }
    public string LanguageCode { get; private set; } = "en";

    public int Transcribe => Id("<|transcribe|>");
    public int Translate => Id("<|translate|>");
    public int Sot => Id("<|startoftranscript|>");
    public int SotLm => Id("<|startoflm|>");
    public int SotPrev => Id("<|startofprev|>");
    public int Eot => Id("<|endoftext|>");
    public int NoTimestamps => Id("<|notimestamps|>");
    public int NoSpeech => TokenToId("<|nospeech|>") is { } id and not 0 ? id : Id("<|nocaptions|>");
    public int TimestampBegin => NoTimestamps + 1;

    public int[] SotSequence
    {
        get
        {
            List<int> sequence = [Sot];
            if (Language is { } language)
                sequence.Add(language);
            if (Task is { } task)
                sequence.Add(task);
            return [.. sequence];
        }
    }

    public void SetLanguage(string code)
    {
        Language = TokenToId($"<|{code}|>") ?? throw new ArgumentException($"unknown language {code}");
        LanguageCode = code;
    }

    public int? TokenToId(string token) =>
        _added.TryGetValue(token, out var id) ? id : _vocab.TryGetValue(token, out id) ? id : null;

    private int Id(string token) => TokenToId(token) ?? throw new InvalidOperationException($"no token {token}");

    /// <summary>Token ids of plain text, without special tokens.</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        foreach (Match piece in PreTokenize().Matches(text))
        {
            var mapped = new string(Encoding.UTF8.GetBytes(piece.Value).Select(b => ByteToChar[b]).ToArray());
            foreach (var symbol in Bpe(mapped))
                ids.Add(_vocab[symbol]);
        }
        return ids;
    }

    /// <summary>Text of the tokens below end-of-text (faster-whisper's Tokenizer.decode).</summary>
    public string Decode(IEnumerable<int> tokens) => DecodeRaw(tokens.Where(t => t < Eot));

    /// <summary>The Hugging Face decode with skip_special_tokens: special tokens dropped, bytes joined, lossy UTF-8.</summary>
    public string DecodeRaw(IEnumerable<int> tokens)
    {
        var bytes = new List<byte>();
        foreach (var id in tokens)
        {
            if (_special.Contains(id) || id < 0 || id >= _idToToken.Length || _idToToken[id] is not { } token)
                continue;
            foreach (var c in token)
            {
                if (CharToByte.TryGetValue(c, out var b))
                    bytes.Add(b);
                else
                    bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    public string DecodeWithTimestamps(IReadOnlyList<int> tokens)
    {
        var sb = new StringBuilder();
        var run = new List<int>();
        foreach (var token in tokens)
        {
            if (token >= TimestampBegin)
            {
                sb.Append(DecodeRaw(run));
                run.Clear();
                sb.Append("<|").Append(((token - TimestampBegin) * 0.02).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append("|>");
            }
            else
            {
                run.Add(token);
            }
        }
        sb.Append(DecodeRaw(run));
        return sb.ToString();
    }

    /// <summary>
    /// Tokens to suppress so the model does not write speaker tags or non-speech annotations (♪♪♪,
    /// "( SPEAKING FOREIGN LANGUAGE )", "[DAVID] Hey there"), keeping basic punctuation.
    /// </summary>
    public int[] NonSpeechTokens()
    {
        var symbols = "\"#()*+/:;<=>@[\\]^_`{|}~「」『』".Select(c => c.ToString()).ToList();
        symbols.AddRange("<< >> <<< >>> -- --- -( -[ (' (\" (( )) ((( ))) [[ ]] {{ }} ♪♪ ♪♪♪".Split(' '));
        var miscellaneous = "♩♪♫♬♭♮♯".Select(c => c.ToString()).ToHashSet();
        // allow hyphens "-" and single quotes "'" between words, but not at the beginning of a word
        var result = new SortedSet<int> { Encode(" -")[0], Encode(" '")[0] };
        foreach (var symbol in symbols.Concat(miscellaneous))
        {
            foreach (var tokens in new[] { Encode(symbol), Encode(" " + symbol) })
            {
                if (tokens.Count == 1 || miscellaneous.Contains(symbol))
                    result.Add(tokens[0]);
            }
        }
        return [.. result];
    }

    /// <summary>Split tokens into words: at spaces and punctuation, keeping multi-byte characters whole.</summary>
    public (List<string> Words, List<List<int>> Tokens) SplitToWordTokens(IReadOnlyList<int> tokens)
    {
        if (LanguageCode is "zh" or "ja" or "th" or "lo" or "my" or "yue")
            return SplitTokensOnUnicode(tokens);
        return SplitTokensOnSpaces(tokens);
    }

    private const string Punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~"; // Python's string.punctuation

    private (List<string>, List<List<int>>) SplitTokensOnSpaces(IReadOnlyList<int> tokens)
    {
        var (subwords, subwordTokens) = SplitTokensOnUnicode(tokens);
        var words = new List<string>();
        var wordTokens = new List<List<int>>();
        for (var i = 0; i < subwords.Count; i++)
        {
            var subword = subwords[i];
            var special = subwordTokens[i][0] >= Eot;
            var withSpace = subword.StartsWith(' ');
            var punctuation = Punctuation.Contains(Subtitles.Py.Strip(subword), StringComparison.Ordinal);
            if (special || withSpace || punctuation || words.Count == 0)
            {
                words.Add(subword);
                wordTokens.Add(subwordTokens[i]);
            }
            else
            {
                words[^1] += subword;
                wordTokens[^1].AddRange(subwordTokens[i]);
            }
        }
        return (words, wordTokens);
    }

    private (List<string>, List<List<int>>) SplitTokensOnUnicode(IReadOnlyList<int> tokens)
    {
        var decodedFull = DecodeWithTimestamps(tokens);
        const char replacement = '�';
        var words = new List<string>();
        var wordTokens = new List<List<int>>();
        var current = new List<int>();
        var unicodeOffset = 0;
        foreach (var token in tokens)
        {
            current.Add(token);
            var decoded = DecodeWithTimestamps(current);
            var index = decoded.IndexOf(replacement);
            int? replacementIndex = index >= 0 ? index + unicodeOffset : null;
            if (replacementIndex is null
                || (replacementIndex < decodedFull.Length && decodedFull[replacementIndex.Value] == replacement))
            {
                words.Add(decoded);
                wordTokens.Add(current);
                current = [];
                unicodeOffset += decoded.Length;
            }
        }
        return (words, wordTokens);
    }

    private List<string> Bpe(string word)
    {
        var symbols = word.Select(c => c.ToString()).ToList();
        while (symbols.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;
            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (_mergeRanks.TryGetValue((symbols[i], symbols[i + 1]), out var r) && r < bestRank)
                    (bestRank, bestIndex) = (r, i);
            }
            if (bestIndex < 0)
                break;
            var (left, right) = (symbols[bestIndex], symbols[bestIndex + 1]);
            var merged = new List<string>(symbols.Count);
            for (var i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == left && symbols[i + 1] == right)
                {
                    merged.Add(left + right);
                    i++;
                }
                else
                {
                    merged.Add(symbols[i]);
                }
            }
            symbols = merged;
        }
        return symbols;
    }

    private static (string, string) SplitMerge(string merge)
    {
        var space = merge.IndexOf(' ', 1);
        return (merge[..space], merge[(space + 1)..]);
    }

    /// <summary>GPT-2's reversible byte to printable character table.</summary>
    private static char[] BuildByteToChar()
    {
        var table = new char[256];
        var printable = new HashSet<int>();
        for (var b = '!'; b <= '~'; b++)
            printable.Add(b);
        for (var b = 0xA1; b <= 0xAC; b++)
            printable.Add(b);
        for (var b = 0xAE; b <= 0xFF; b++)
            printable.Add(b);
        var n = 0;
        for (var b = 0; b < 256; b++)
            table[b] = printable.Contains(b) ? (char)b : (char)(256 + n++);
        return table;
    }

    [GeneratedRegex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+")]
    private static partial Regex PreTokenize();
}
