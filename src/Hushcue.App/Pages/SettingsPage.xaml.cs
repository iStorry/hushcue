using System.Diagnostics;
using Hushcue.Core;
using Hushcue.Core.Pipeline;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Pages;

/// <summary>
/// Choices that rarely change between runs (language, vocabulary, clean-up, what happens to the files), the theme, and
/// this PC's engine and data. Every change is written to the shared <see cref="AppSettings"/> straight away.
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private AppSettings _settings = new();
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var (name, _) in Presets.Languages)
            LanguageBox.Items.Add(name);
        DataRow.Description = DataFolder;
    }

    /// <summary>Something changed (also the theme): the other pages refresh what they show of it.</summary>
    public event EventHandler? Changed;

    public event EventHandler? SystemCheckRequested;

    private static string DataFolder => Path.GetDirectoryName(Models.Folder)!;

    public void Load(AppSettings settings)
    {
        _settings = settings;
        _loading = true;
        LanguageBox.SelectedItem = Presets.Languages.Any(l => l.Name == settings.Language) ? settings.Language : Presets.Languages[0].Name;
        VocabularyBox.Text = settings.Vocabulary;
        EnhanceToggle.IsOn = settings.Enhance;
        SkipSilenceToggle.IsOn = settings.SkipSilence;
        KeepSrtToggle.IsOn = settings.KeepSrt;
        DeleteToggle.IsOn = settings.DeleteOriginal;
        UpdatesToggle.IsOn = settings.CheckForUpdates;
        ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.Theme)
                                ?? ThemeBox.Items[0];
        _loading = false;
    }

    /// <summary>"On your NVIDIA GeForce RTX 4070" or why it runs on the CPU, from the system check.</summary>
    public void SetDevice(string text) => DeviceRow.Description = text;

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _settings.Language = LanguageBox.SelectedItem as string ?? _settings.Language;
        _settings.Vocabulary = VocabularyBox.Text;
        _settings.Enhance = EnhanceToggle.IsOn;
        _settings.SkipSilence = SkipSilenceToggle.IsOn;
        _settings.KeepSrt = KeepSrtToggle.IsOn;
        _settings.DeleteOriginal = DeleteToggle.IsOn;
        _settings.CheckForUpdates = UpdatesToggle.IsOn;
        _settings.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSystemCheck(object sender, RoutedEventArgs e) => SystemCheckRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DataFolder);
        Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });
    }
}
