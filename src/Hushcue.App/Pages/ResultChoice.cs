using Hushcue.Core.Media;

namespace Hushcue.Pages;

/// <summary>One way to deliver the subtitles, shown as a card on the Generate page; set from XAML.</summary>
public sealed class ResultChoice
{
    public EmbedMode Mode { get; set; }
    public string Glyph { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}
