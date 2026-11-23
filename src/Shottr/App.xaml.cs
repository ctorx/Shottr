using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Shottr.Capture;
using Shottr.Core;
using Shottr.Settings;
using Shottr.Update;

namespace Shottr;

/// <summary>
/// Shottr runs in the notification area. Global hotkeys start a capture, and every capture
/// opens directly in the editor.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Passed to the new copy after a self-update, followed by the version it replaced. The
    /// new copy waits for the old one to exit before it takes the single-instance lock.
    /// </summary>
    public const string UpdatedFromArg = "--updated-from";

    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;

    public AppSettings Settings { get; private set; } = new();

    internal CaptureService Capture { get; private set; } = null!;

    internal UpdateController Updates { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        string? updatedFrom = ArgValue(e.Args, UpdatedFromArg);
        _singleInstance = new Mutex(true, "Shottr.SingleInstance.5B1E7C2A", out bool isFirst);
        if (!isFirst && updatedFrom != null)
            isFirst = WaitForPreviousInstance(_singleInstance);
        if (!isFirst)
        {
            Shutdown();
            return;
        }
        _ownsSingleInstance = true;

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        // Diagnostic switch: SHOTTR_SOFTWARE=1 turns off GPU rendering, for machines or remote
        // sessions where hardware-rendered WPF windows show up blank.
        if (Environment.GetEnvironmentVariable("SHOTTR_SOFTWARE") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        Settings = AppSettings.Load();
        Capture = new CaptureService(Settings);
        _hotkeys = new HotkeyManager();
        _tray = new TrayIcon(this);
        Updates = new UpdateController(this, _tray);
        Updater.CleanUpAfterUpdate();
        ThemeHelper.WatchThemeChanges(this);

        List<string> failed = RegisterHotkeys();
        if (failed.Count > 0)
        {
            string hint = failed.Any(f => f.Contains("Print Screen")) && AppSettings.WindowsUsesPrintScreen
                ? " Windows is using Print Screen to open Snipping Tool. You can turn that off in Shottr settings."
                : " Another app is using them. You can pick different keys in Shottr settings.";
            _tray.ShowBalloon("Some shortcuts are unavailable", string.Join(", ", failed) + "." + hint);
        }
        else if (updatedFrom != null)
        {
            _tray.ShowBalloon("Shottr was updated", $"You now have version {Updater.CurrentVersion}. You had {UpdateCore.StripVersionPrefix(updatedFrom)}.");
        }
        else if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shottr", "settings.json")))
        {
            _tray.ShowBalloon("Shottr is running", $"Press {Settings.RegionHotkey} to capture a region.");
        }
        Settings.Save();

        if (e.Args.Contains("--region"))
            Capture.CaptureRegion();
        else if (e.Args.Contains("--fullscreen"))
            Capture.CaptureFullScreen();
        else if (e.Args.Contains("--settings"))
            ShowSettings();
        else
            OpenFilesFromArgs(e.Args);

        Updates.StartAutomaticCheck();
    }

    /// <summary>Opens every image named by an "--open &lt;path&gt;" pair, such as editors reopened after an update.</summary>
    private void OpenFilesFromArgs(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] != "--open")
                continue;
            string path = args[++i];
            try
            {
                Capture.OpenFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException)
            {
                _tray?.ShowBalloon("Shottr could not open an image", $"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// After an update, the old copy starts the new one and then exits. This waits up to
    /// 15 seconds for the old copy to release the single-instance lock.
    /// </summary>
    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            // The old copy exited without releasing the lock, and this thread now owns it.
            return true;
        }
    }

    /// <summary>Registers the three capture hotkeys. Returns the names of any that failed.</summary>
    public List<string> RegisterHotkeys()
    {
        var failed = new List<string>();
        if (_hotkeys == null)
            return failed;
        _hotkeys.UnregisterAll();
        if (!_hotkeys.Register(Settings.RegionHotkey, () => Capture.CaptureRegion()))
            failed.Add(Settings.RegionHotkey.ToString());
        if (!_hotkeys.Register(Settings.WindowHotkey, () => Capture.CaptureActiveWindow()))
            failed.Add(Settings.WindowHotkey.ToString());
        if (!_hotkeys.Register(Settings.FullScreenHotkey, () => Capture.CaptureFullScreen()))
            failed.Add(Settings.FullScreenHotkey.ToString());
        _tray?.UpdateHotkeyText(Settings);
        return failed;
    }

    /// <summary>Releases the hotkeys, so the settings window can record new ones.</summary>
    public void SuspendHotkeys() => _hotkeys?.UnregisterAll();

    public void ShowSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void OpenCapturesFolder()
    {
        Directory.CreateDirectory(Settings.SaveFolder);
        Process.Start("explorer.exe", $"\"{Settings.SaveFolder}\"");
    }

    public void ExitApp()
    {
        Updates?.Dispose();
        Settings.Save();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Updates?.Dispose();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        // Releasing the lock right away lets a copy started by a self-update take over sooner.
        if (_ownsSingleInstance)
        {
            try
            {
                _singleInstance?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread. Process exit releases it anyway.
            }
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shottr");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch (IOException)
        {
        }
        MessageBox.Show(e.Exception.Message, "Shottr ran into a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
