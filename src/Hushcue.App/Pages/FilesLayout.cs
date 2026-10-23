using Hushcue.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Pages;

/// <summary>
/// The layout of a page with a file list (Generate subtitles, MKV to MP4): the files in the first cell of the grid and
/// the cards shown with them in a column each beside the files, or on a narrow page under the files. A window too short
/// for it scrolls the page as a whole (<see cref="ScrollFill"/>) instead of squeezing the list and cards.
/// </summary>
internal sealed class FilesLayout
{
    /// <summary>Page width (effective pixels) below which the cards go under the files.</summary>
    private const double StackedBelowWidth = 720;
    private const double CardMinWidth = 330;
    private const double CardMaxWidth = 560;
    private const double SideBySideMinHeight = 420;
    /// <summary>Files and cards each get about half of it.</summary>
    private const double StackedMinHeight = 860;

    private readonly ScrollViewer _scroller;
    private readonly Grid _grid;
    private IReadOnlyList<FrameworkElement> _cards = [];
    private bool _stacked;

    /// <param name="scroller">the page's scroller, holding <paramref name="grid"/></param>
    /// <param name="grid">the files (row 0, column 0) and the cards; its rows and columns are set here</param>
    public FilesLayout(ScrollViewer scroller, Grid grid)
    {
        (_scroller, _grid) = (scroller, grid);
        scroller.SizeChanged += (_, e) =>
        {
            var stacked = e.NewSize.Width < StackedBelowWidth;
            if (stacked == _stacked)
                return;
            _stacked = stacked;
            Show(_cards);
        };
    }

    /// <summary>Lay out these cards with the files, in this order; the page hides the others.</summary>
    public void Show(IReadOnlyList<FrameworkElement> cards)
    {
        _cards = cards;
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        // only as many columns or rows as there are cards: the grid's spacing also goes around empty ones
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        _grid.RowDefinitions.Add(new RowDefinition());
        for (var i = 0; i < cards.Count; i++)
        {
            if (_stacked)
                _grid.RowDefinitions.Add(new RowDefinition());
            else
                _grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(2, GridUnitType.Star),
                    MinWidth = CardMinWidth,
                    MaxWidth = CardMaxWidth,
                });
            Grid.SetColumn(cards[i], _stacked ? 0 : i + 1);
            Grid.SetRow(cards[i], _stacked ? i + 1 : 0);
        }
        ScrollFill.SetMinHeight(_scroller, _stacked ? StackedMinHeight : SideBySideMinHeight);
    }
}
