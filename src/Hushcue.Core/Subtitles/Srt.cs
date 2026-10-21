namespace Hushcue.Core.Subtitles;

public static class Srt
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
}
