using System.ComponentModel;
using Hushcue.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hushcue.Controls;

/// <summary>A file in the queue and where it is: waiting, being worked on, done, or failed.</summary>
public sealed class QueuedFile(string path) : INotifyPropertyChanged
{
    private const string WaitingGlyph = "";
    private const string WorkingGlyph = "";
    private const string DoneGlyph = "";
    private const string FailedGlyph = "";

    private FileProgress _progress = FileProgress.Waiting(path);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public FileState State => _progress.State;
    public string? Output => _progress.Output;

    /// <summary>The line under the name: the folder while waiting, otherwise what is happening to the file.</summary>
    public string Detail => State == FileState.Waiting ? System.IO.Path.GetDirectoryName(Path) ?? "" : _progress.Status;

    public double Percent => _progress.Fraction * 100;
    public string PercentText => State == FileState.Working ? $"{Percent:F0}%" : "";
    public Visibility ProgressVisibility => VisibleIf(State == FileState.Working);
    public Visibility OutputVisibility => VisibleIf(Output is not null);

    public string Glyph => State switch
    {
        FileState.Working => WorkingGlyph,
        FileState.Done => DoneGlyph,
        FileState.Failed => FailedGlyph,
        _ => WaitingGlyph,
    };

    public Brush GlyphBrush => ThemeBrushes.Get(State switch
    {
        FileState.Done => ThemeBrushes.Success,
        FileState.Failed => ThemeBrushes.Critical,
        _ => ThemeBrushes.Secondary,
    });

    public void Update(FileProgress progress)
    {
        _progress = progress;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // every property follows the progress
    }

    public void Requeue() => Update(FileProgress.Waiting(Path));

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
