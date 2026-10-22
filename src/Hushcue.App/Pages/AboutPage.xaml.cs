using System.Diagnostics;
using Hushcue.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Pages;

/// <summary>What Hushcue is, its version, what it is built on, and where to find the source and licences.</summary>
public sealed partial class AboutPage : UserControl
{
    private const string Repository = "https://github.com/iStorry/hushcue";

    public AboutPage()
    {
        InitializeComponent();
        NameText.Text = Product.Name;
        TaglineText.Text = Product.Tagline;
        var version = Core.Updates.Updates.Current.ToString(3);
        VersionText.Text = $"Version {version}";
        Loaded += async (_, _) => IconImage.Source ??= await AppIcon.LoadAsync();
        NoticesButton.Visibility = File.Exists(NoticesPath) ? Visibility.Visible : Visibility.Collapsed;
    }

    public event EventHandler? UpdateCheckRequested;

    private void OnCheckForUpdates(object sender, RoutedEventArgs e) => UpdateCheckRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shipped next to the exe by tools/publish.ps1 (a development build has none).</summary>
    private static string NoticesPath => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");

    private void OnSource(object sender, RoutedEventArgs e) => Open(Repository);

    private void OnIssues(object sender, RoutedEventArgs e) => Open(Repository + "/issues");

    private void OnNotices(object sender, RoutedEventArgs e) => Open(NoticesPath);

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
