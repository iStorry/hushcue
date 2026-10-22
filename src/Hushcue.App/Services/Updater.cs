using System.Diagnostics;
using Hushcue.Core;
using Hushcue.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hushcue.Services;

/// <summary>
/// "A new version is available. Update now?": checks GitHub's latest release (on start at most once a day, or when
/// asked), downloads the installer with progress, and hands over to it. A portable copy opens the release page instead.
/// </summary>
internal sealed class Updater(AppSettings settings, Func<Task<bool>> prepareToExit)
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);
    private bool _busy;

    /// <summary>On start: quietly, only when due and enabled; a skipped version is not offered again.</summary>
    public async Task CheckOnStartAsync(XamlRoot root)
    {
        if (!settings.CheckForUpdates || settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < CheckEvery)
            return;
        await CheckAsync(root, manual: false);
    }

    /// <summary>"Check for updates" in About: says so when there is nothing new or the check fails.</summary>
    public Task CheckNowAsync(XamlRoot root) => CheckAsync(root, manual: true);

    private async Task CheckAsync(XamlRoot root, bool manual)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            ReleaseInfo? release;
            try
            {
                release = await Task.Run(() => Updates.CheckAsync());
            }
            catch (UpdateException e)
            {
                if (manual)
                    await Dialogs.ErrorAsync(root, e.Message);
                return;
            }
            settings.LastUpdateCheck = DateTimeOffset.Now;
            if (release is null)
            {
                if (manual)
                    await Dialogs.InfoAsync(root, $"You have the latest version of {Product.Name} ({Updates.Current.ToString(3)}).");
                return;
            }
            if (!manual && settings.SkippedVersion == release.Tag)
                return;
            await OfferAsync(root, release, manual);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task OfferAsync(XamlRoot root, ReleaseInfo release, bool manual)
    {
        var installed = Updates.IsInstalled(AppContext.BaseDirectory) && release.Installer is not null;
        var notes = release.Notes.Trim();
        if (notes.Length > 700)
            notes = notes[..700].TrimEnd() + "…";
        var text = $"{Product.Name} {release.Version.ToString(3)} is available. You have {Updates.Current.ToString(3)}."
                   + (notes.Length > 0 ? "\n\n" + notes : "")
                   + (installed ? "\n\nThe update downloads, Hushcue closes, installs it and opens again."
                                : "\n\nThis copy wasn't installed with the installer: get the new version from the release page.");
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Update available",
            Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, MaxHeight = 360 },
            PrimaryButtonText = installed ? "Update now" : "Open the release page",
            SecondaryButtonText = manual ? "" : "Skip this version",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        var choice = await ShowAsync(dialog);
        if (choice == ContentDialogResult.Secondary)
        {
            settings.SkippedVersion = release.Tag;
            return;
        }
        if (choice != ContentDialogResult.Primary)
            return;
        if (!installed)
        {
            Process.Start(new ProcessStartInfo(release.Page.ToString()) { UseShellExecute = true });
            return;
        }
        await InstallAsync(root, release);
    }

    private async Task InstallAsync(XamlRoot root, ReleaseInfo release)
    {
        var bar = new ProgressBar { Maximum = 1, Value = 0 };
        var status = new TextBlock { Text = "Downloading...", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        var cancel = new CancellationTokenSource();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Downloading {Product.Name} {release.Version.ToString(3)}",
            Content = new StackPanel { Spacing = 10, MinWidth = 360, Children = { bar, status } },
            CloseButtonText = "Cancel",
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        dialog.CloseButtonClick += (_, _) => cancel.Cancel();
        var ui = root.Content.DispatcherQueue;
        var shown = ShowAsync(dialog);
        string installer;
        try
        {
            installer = await Task.Run(() => Updates.DownloadInstallerAsync(release, (done, total) => ui.TryEnqueue(() =>
            {
                bar.Value = total > 0 ? (double)done / total : 0;
                status.Text = $"{done / 1048576.0:F1} of {total / 1048576.0:F1} MB";
            }), cancel.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (UpdateException e)
        {
            dialog.Hide();
            await shown;
            await Dialogs.ErrorAsync(root, e.Message);
            return;
        }
        dialog.Hide();
        await shown;
        if (!await prepareToExit())
            return; // the user chose to keep a running batch or unsaved edits; the download stays for next time
        Program.Instance?.Dispose(); // the installer waits for Hushcue's mutex to be gone
        Updates.RunInstaller(installer);
        Application.Current.Exit();
    }

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return ContentDialogResult.None; // another dialog is open: offer again next time
        }
    }
}
