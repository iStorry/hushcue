namespace Hushcue.Core.Subtitles;

public static partial class Srt
{
    /// <summary>SubRip text: numbered cues, hh:mm:ss,mmm times, each cue wrapped to at most two lines.</summary>
    public static string Write(IEnumerable<Cue> cues)
    {
        static string Ts(double t)
        {
            var ms = Py.Round(Math.Max(t, 0) * 1000);
            var h = Math.DivRem(ms, 3_600_000, out ms);
            var m = Math.DivRem(ms, 60_000, out ms);
            var s = Math.DivRem(ms, 1000, out ms);
            return $"{h:00}:{m:00}:{s:00},{ms:000}";
        }

        var blocks = cues.Select((c, i) => $"{i + 1}\n{Ts(c.Start)} --> {Ts(c.End)}\n{Cleanup.WrapLines(c.Text)}\n");
        return string.Join("\n", blocks);
    }

    /// <summary>Write an .srt file with CRLF line endings, as SubGen's Python writes text files on Windows.</summary>
    public static Task SaveAsync(string path, IEnumerable<Cue> cues, CancellationToken cancel = default) =>
        File.WriteAllTextAsync(path, Write(cues).Replace("\n", "\r\n"), cancel);

    /// <summary>
    /// The cues of SubRip text, in the order written: each block's "start --> end" line and the text lines under it,
    /// joined with a space (the inverse of <see cref="Write"/>, whose line wrapping is only for display). Accepts LF
    /// or CRLF, a byte order mark, '.' for ',' and missing numbers; skips blocks without a time line or text.
    /// </summary>
    public static List<Cue> Read(string text)
    {
        var cues = new List<Cue>();
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var times = TimeLine().Match(lines[i]);
            if (!times.Success)
                continue;
            var body = new List<string>();
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0)
                body.Add(lines[++i].Trim());
            var cueText = string.Join(' ', body);
            if (cueText.Length > 0)
                cues.Add(new Cue(Seconds(times.Groups[1].Value), Seconds(times.Groups[2].Value), cueText));
        }
        return cues;
    }

    /// <summary><see cref="Read"/> from a file.</summary>
    public static async Task<List<Cue>> LoadAsync(string path, CancellationToken cancel = default) =>
        Read(await File.ReadAllTextAsync(path, cancel));

    private static double Seconds(string stamp)
    {
        var parts = stamp.Replace(',', '.').Split(':');
        var seconds = double.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[^2], System.Globalization.CultureInfo.InvariantCulture);
        var hours = parts.Length > 2 ? int.Parse(parts[^3], System.Globalization.CultureInfo.InvariantCulture) : 0;
        return Math.Round(hours * 3600 + minutes * 60 + seconds, 3);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*((?:\d+:)?\d{1,2}:\d{1,2}[,.]\d{1,3})\s*-->\s*((?:\d+:)?\d{1,2}:\d{1,2}[,.]\d{1,3})")]
    private static partial System.Text.RegularExpressions.Regex TimeLine();
}
