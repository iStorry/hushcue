using System.ComponentModel;
using Hushcue.Core.Review;
using Hushcue.Core.Subtitles;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Hushcue.Pages;

/// <summary>
/// A line of the review screen's list: a subtitle (with its flag, if any), or a stretch without subtitles worth
/// checking (a gap, or the end of a file that looks cut short).
/// </summary>
public sealed class ReviewRow : INotifyPropertyChanged
{
    private bool _current;

    private ReviewRow(int cue, double start, double end, string text, CueFlag? flag)
    {
        (Cue, Start, End, Text, Flag) = (cue, start, end, text, flag);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The subtitle's index, or -1 for a stretch without subtitles.</summary>
    public int Cue { get; private set; }
    public double Start { get; private set; }
    public double End { get; private set; }
    public string Text { get; private set; }
    public CueFlag? Flag { get; private set; }

    public bool IsGap => Cue < 0;

    public static ReviewRow ForCue(int index, Cue cue, CueFlag? flag) => new(index, cue.Start, cue.End, cue.Text, flag);

    public static ReviewRow ForGap(CueFlag flag) => new(-1, flag.Start, flag.End, flag.Message, flag);

    /// <summary>"04:08.2" for a subtitle, "04:19–04:51" for a stretch.</summary>
    public string Time => IsGap ? CueTime.Range(Start, End) : CueTime.Format(Start);

    public string FlagLabel => Flag?.Label ?? "";
    public Visibility FlagVisibility => Flag is null ? Visibility.Collapsed : Visibility.Visible;
    public FontStyle TextStyle => IsGap ? FontStyle.Italic : FontStyle.Normal;
    public Brush TextBrush => ThemeBrushes.Get(IsGap ? ThemeBrushes.Secondary : ThemeBrushes.Primary);

    /// <summary>The subtitle playing now is marked in the list.</summary>
    public bool IsCurrent
    {
        get => _current;
        set
        {
            if (_current == value)
                return;
            _current = value;
            Changed();
        }
    }

    public Brush MarkerBrush => _current ? ThemeBrushes.Get(ThemeBrushes.Accent) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    public FontWeight TimeWeight => _current ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>What a screen reader says: "04:08.2 to 04:10.5, Right, let's pick up..., likely hallucination".</summary>
    public string AccessibleName =>
        string.Join(", ", new[]
        {
            IsGap ? CueTime.Range(Start, End) : $"{CueTime.Format(Start)} to {CueTime.Format(End)}",
            Text,
            Flag is null ? "" : IsGap ? Flag.Label : $"{Flag.Label}: {Flag.Message}",
            _current ? "playing" : "",
        }.Where(p => p.Length > 0));

    /// <summary>Take <paramref name="other"/>'s content, keeping this row object (and the list's place).</summary>
    public void CopyFrom(ReviewRow other)
    {
        if (Cue == other.Cue && Start == other.Start && End == other.End && Text == other.Text && Flag == other.Flag)
            return;
        (Cue, Start, End, Text, Flag) = (other.Cue, other.Start, other.End, other.Text, other.Flag);
        Changed();
    }

    /// <summary>Re-read everything, also the theme's brushes.</summary>
    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public override string ToString() => AccessibleName;
}
