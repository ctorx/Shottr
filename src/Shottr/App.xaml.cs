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

namespace Shottr;

/// <summary>
/// Shottr runs in the notification area. Global hotkeys start a capture, and every capture
/// opens directly in the editor.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;

    public AppSettings Settings { get; private set; } = new();

    internal CaptureService Capture { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Shottr.SingleInstance.5B1E7C2A", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

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
        ThemeHelper.WatchThemeChanges(this);

        List<string> failed = RegisterHotkeys();
        if (failed.Count > 0)
        {
            string hint = failed.Any(f => f.Contains("Print Screen")) && AppSettings.WindowsUsesPrintScreen
                ? " Windows is using Print Screen to open Snipping Tool. You can turn that off in Shottr settings."
                : " Another app is using them. You can pick different keys in Shottr settings.";
            _tray.ShowBalloon("Some shortcuts are unavailable", string.Join(", ", failed) + "." + hint);
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
        else if (Array.IndexOf(e.Args, "--open") is int i and >= 0 && i + 1 < e.Args.Length)
            Capture.OpenFile(e.Args[i + 1]);
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
        Settings.Save();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hotkeys?.Dispose();
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
