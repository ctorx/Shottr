using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Shottr.Core;
using Shottr.Editor;

namespace Shottr.Update;

/// <summary>
/// Runs update checks and installs from the UI side. Network work runs asynchronously, and
/// every await resumes on the UI thread, so the prompts and tray changes are thread safe.
/// </summary>
internal sealed class UpdateController : IDisposable
{
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);
    // Waits a little after startup so the automatic check does not compete with sign-in.
    private static readonly TimeSpan AutoCheckDelay = TimeSpan.FromSeconds(20);

    private const string Title = "Shottr update";

    private readonly App _app;
    private readonly TrayIcon _tray;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _busy;

    public UpdateController(App app, TrayIcon tray)
    {
        _app = app;
        _tray = tray;
    }

    /// <summary>True while a check or download is running. The tray and Settings disable their commands.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            _busy = value;
            _tray.SetUpdateBusy(value);
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? BusyChanged;

    // ---- Automatic check --------------------------------------------------------------------

    /// <summary>
    /// Checks in the background at most once a day, when the setting allows it. Shows a tray
    /// notice only when an update exists, and installs nothing without the user's consent.
    /// </summary>
    public async void StartAutomaticCheck()
    {
        AppSettings s = _app.Settings;
        if (!s.CheckForUpdatesAutomatically)
            return;
        if (s.LastUpdateCheckUtc is DateTime last && DateTime.UtcNow - last < AutoCheckInterval)
            return;

        try
        {
            await Task.Delay(AutoCheckDelay, _shutdown.Token);
            if (IsBusy || !_app.Settings.CheckForUpdatesAutomatically)
                return;

            IsBusy = true;
            UpdateCheckResult result;
            try
            {
                result = await Updater.CheckForUpdateAsync(_shutdown.Token);
            }
            finally
            {
                IsBusy = false;
            }

            // A failed check, such as no network, tries again at the next start.
            if (result.Status != UpdateCheckStatus.Failed)
            {
                _app.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                _app.Settings.Save();
            }

            if (result.Status == UpdateCheckStatus.Available && result.Release != null)
            {
                _tray.ShowBalloon(
                    $"Shottr {result.Release.Version} is available",
                    "Click here to download and install it.",
                    () => OfferUpdate(result));
            }
        }
        catch (OperationCanceledException)
        {
            // Shottr is exiting.
        }
    }

    // ---- Manual check -----------------------------------------------------------------------

    /// <summary>Runs the "Check for Updates…" command and reports every outcome.</summary>
    public async void CheckNow()
    {
        if (IsBusy)
            return;

        UpdateCheckResult result;
        IsBusy = true;
        try
        {
            result = await Updater.CheckForUpdateAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (result.Status != UpdateCheckStatus.Failed)
        {
            _app.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _app.Settings.Save();
        }

        switch (result.Status)
        {
            case UpdateCheckStatus.UpToDate:
                Show($"You have the latest version ({Updater.CurrentVersion}).", MessageBoxImage.Information);
                break;
            case UpdateCheckStatus.NoReleases:
                Show($"No Shottr releases have been published yet. You have version {Updater.CurrentVersion}.", MessageBoxImage.Information);
                break;
            case UpdateCheckStatus.RateLimited:
                Show("GitHub is limiting update checks from this network right now. Try again later.", MessageBoxImage.Warning);
                break;
            case UpdateCheckStatus.Failed:
                ShowFailure(UpdateStep.Check, result.Reason ?? "Unknown error.");
                break;
            case UpdateCheckStatus.Available:
                OfferUpdate(result);
                break;
        }
    }

    // ---- Install flow -----------------------------------------------------------------------

    private async void OfferUpdate(UpdateCheckResult result)
    {
        if (IsBusy || result.Release == null)
            return;
        ReleaseInfo release = result.Release;
        string latest = release.Version;
        string current = Updater.CurrentVersion;

        if (result.Asset == null)
        {
            if (Ask($"Shottr {latest} is available, but it has no download for this computer. You have {current}.\n\nOpen the release page in your browser?",
                    MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                OpenInBrowser(release.PageUrl);
            return;
        }

        if (Ask($"Shottr {latest} is available. You have {current}.\n\nDownload and install it now? Shottr will restart.",
                MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        DownloadedUpdate downloaded;
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => _tray.SetStatusText($"Shottr: downloading update ({p:P0})"));
            downloaded = await Updater.DownloadUpdateAsync(release, result.Asset, progress, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (UpdateException ex)
        {
            ShowFailure(ex.Step, ex.Message);
            return;
        }
        finally
        {
            _tray.SetStatusText(null);
            IsBusy = false;
        }

        // Unsaved screenshots come first. Cancel abandons the update.
        List<string>? reopen = PrepareEditorsForRestart();
        if (reopen == null)
        {
            Updater.Discard(downloaded);
            return;
        }

        // The new process reads settings from disk, so save them before the swap.
        _app.Settings.Save();

        try
        {
            Updater.InstallUpdate(downloaded);
        }
        catch (UpdateException ex)
        {
            ShowFailure(ex.Step, ex.Message);
            return;
        }

        var args = new List<string> { App.UpdatedFromArg, current };
        foreach (string path in reopen)
        {
            args.Add("--open");
            args.Add(path);
        }

        try
        {
            Updater.Relaunch(args);
        }
        catch (UpdateException ex)
        {
            // This copy keeps running. The new exe is already in place for the next start.
            Show($"Shottr {latest} is installed, but it could not be started. {ex.Message}\n\n" +
                 "This copy of Shottr will keep running. The new version will start the next time you open Shottr.",
                 MessageBoxImage.Warning);
            return;
        }

        _app.ExitApp();
    }

    /// <summary>
    /// Asks about each editor window with unsaved changes: save to the captures folder,
    /// discard, or cancel. Returns the saved image paths for the new process to reopen, or
    /// null when the user cancels or a save fails.
    /// </summary>
    private List<string>? PrepareEditorsForRestart()
    {
        var editors = _app.Windows.OfType<EditorWindow>().ToList();
        var reopen = new List<string>();
        foreach (EditorWindow editor in editors)
        {
            if (editor.HasUnsavedChanges)
            {
                editor.Activate();
                MessageBoxResult answer = MessageBox.Show(editor,
                    $"This screenshot has unsaved changes. Save it to {_app.Settings.SaveFolder} before Shottr restarts?\n\n" +
                    "Yes saves it, No discards it, and Cancel stops the update.",
                    Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None)
                    return null;
                if (answer == MessageBoxResult.Yes && !editor.SaveToCapturesFolder())
                    return null; // The editor shows why the save failed.
            }
            if (!editor.HasUnsavedChanges && editor.SavedPath != null)
                reopen.Add(editor.SavedPath);
        }
        return reopen;
    }

    // ---- Messages ---------------------------------------------------------------------------

    private static void ShowFailure(UpdateStep step, string reason)
    {
        string what = step switch
        {
            UpdateStep.Check => "Shottr could not check for updates.",
            UpdateStep.Download => "Shottr could not download the update.",
            UpdateStep.Verification => "Shottr downloaded the update, but the file failed verification, so it was deleted.",
            _ => "Shottr could not install the update.",
        };
        Show($"{what}\n\n{reason}", MessageBoxImage.Warning);
    }

    private static void Show(string message, MessageBoxImage image) =>
        MessageBox.Show(message, Title, MessageBoxButton.OK, image);

    private static MessageBoxResult Ask(string message, MessageBoxButton buttons) =>
        MessageBox.Show(message, Title, buttons, MessageBoxImage.Question, MessageBoxResult.Yes);

    private static void OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            url = $"https://github.com/{Updater.Repository}/releases/latest";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Show($"Shottr could not open your browser. The release page is {url}", MessageBoxImage.Warning);
        }
    }

    /// <summary>Cancels any running check or download when Shottr exits.</summary>
    public void Dispose()
    {
        // The source is cancelled but not disposed, because a check that is finishing may
        // still read its token.
        _shutdown.Cancel();
    }
}
