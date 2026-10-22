using System.ComponentModel;
using Hushcue.Core;
using Hushcue.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hushcue.Controls;

/// <summary>What the next run will do with a file, from its probe: null when all is well.</summary>
/// <param name="Skip">the run leaves the file out (it can't be read, or has no sound)</param>
public sealed record FileCheck(string Warning, bool Skip);

/// <summary>A file in the queue and where it is: waiting, being worked on, done, or failed.</summary>
public sealed class QueuedFile(string path) : INotifyPropertyChanged
{
    private const string WaitingGlyph = "\uE823"; // clock
    private const string WarningGlyph = "\uE7BA";
    private const string WorkingGlyph = "\uE768"; // play
    private const string DoneGlyph = "\uE73E"; // check mark
    private const string FailedGlyph = "\uE783";

    private FileProgress _progress = FileProgress.Waiting(path);
    private bool _locked;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public FileState State => _progress.State;
    public string? Output => _progress.Output;

    /// <summary>Length and streams, once the probe that starts when the file is added has finished.</summary>
    public MediaInfo? Info { get; private set; }
    public string? ProbeError { get; private set; }
    public Task Probing { get; set; } = Task.CompletedTask;
    public FileCheck? Check { get; private set; }

    /// <summary>Seconds, 0 until probed or when the file does not say.</summary>
    public double Duration => Info?.Duration ?? 0;

    /// <summary>The line under the name: what is happening to the file, or what will.</summary>
    public string Detail => State switch
    {
        FileState.Working => Tidy(_progress.Status),
        FileState.Done => Join("Done", Lower(_progress.Status), Length),
        FileState.Failed => _progress.Status,
        _ when Check is not null => Check.Warning,
        _ => Join(ProbeError is null && Info is null ? "Waiting · reading the file..." : "Waiting", Length),
    };

    public Brush DetailBrush => ThemeBrushes.Get(State switch
    {
        FileState.Failed => ThemeBrushes.Critical,
        FileState.Waiting when Check is not null => ThemeBrushes.Caution,
        _ => ThemeBrushes.Secondary,
    });

    /// <summary>Working, but only reading the audio or loading the model (Core's JobRun status texts).</summary>
    public bool IsStarting => State == FileState.Working
                              && (_progress.Status.StartsWith("Extracting audio", StringComparison.Ordinal)
                                  || _progress.Status.StartsWith("Loading model", StringComparison.Ordinal));

    public double Percent => _progress.Fraction * 100;
    public string PercentText => State == FileState.Working ? $"{Percent:F0}%" : "";
    public Visibility ProgressVisibility => VisibleIf(State == FileState.Working);
    public Visibility OutputVisibility => VisibleIf(State == FileState.Done && Output is not null);
    public Visibility RemoveVisibility => VisibleIf(!_locked && State != FileState.Working);

    public string Glyph => State switch
    {
        FileState.Working => WorkingGlyph,
        FileState.Done => DoneGlyph,
        FileState.Failed => FailedGlyph,
        _ => Check is not null ? WarningGlyph : WaitingGlyph,
    };

    public Brush GlyphBrush => ThemeBrushes.Get(State switch
    {
        FileState.Done => ThemeBrushes.Success,
        FileState.Failed => ThemeBrushes.Critical,
        FileState.Working => ThemeBrushes.Accent,
        _ => Check is not null ? ThemeBrushes.Caution : ThemeBrushes.Secondary,
    });

    /// <summary>What a screen reader says for the row.</summary>
    public string AccessibleName => State == FileState.Working ? $"{Name}, {PercentText}, {Detail}" : $"{Name}, {Detail}";
    public string RemoveName => $"Remove {Name} from the list";
    public string ShowName => $"Show {System.IO.Path.GetFileName(Output ?? Name)} in its folder";

    public void Update(FileProgress progress)
    {
        _progress = progress;
        Changed();
    }

    public void Requeue() => Update(FileProgress.Waiting(Path));

    public void SetProbe(MediaInfo? info, string? error)
    {
        (Info, ProbeError) = (info, error);
        Changed();
    }

    public void SetCheck(FileCheck? check)
    {
        if (check == Check)
            return;
        Check = check;
        Changed();
    }

    /// <summary>While a run is going, files can't be removed from the list.</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        Changed();
    }

    /// <summary>Every property follows the progress; also re-reads the theme brushes when the theme changes.</summary>
    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public override string ToString() => AccessibleName;

    private string Length => Duration > 0 ? BatchEstimate.Clock(TimeSpan.FromSeconds(Duration)) : "";

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));

    private static string Lower(string text) => text.Length > 0 ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    /// <summary>"Transcribing... 52× real time, about 40 s left" reads as "Transcribing · 52× real time · about 40 s left".</summary>
    private static string Tidy(string status) =>
        status.Replace("... ", " · ", StringComparison.Ordinal).Replace(", about ", " · about ", StringComparison.Ordinal);

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
