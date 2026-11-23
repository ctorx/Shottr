using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shottr.Controls;
using Forms = System.Windows.Forms;

namespace Shottr.Core;

/// <summary>
/// The notification-area icon. The menu is a themed WPF menu rather than the WinForms one,
/// so it matches the Windows light/dark setting.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly Window _menuHost;
    private readonly MenuItem _regionItem;
    private readonly MenuItem _windowItem;
    private readonly MenuItem _fullItem;
    private readonly MenuItem _updateItem;
    private Action? _balloonClick;

    public TrayIcon(App app)
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/shottr.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Shottr",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right)
                ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            Action? click = _balloonClick;
            _balloonClick = null;
            click?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;

        _regionItem = Item("Capture region", "Icon.Region", () => CaptureDelayed(app, a => a.Capture.CaptureRegion()));
        // From the menu there is no meaningful "active window", so window capture uses the
        // region picker, where clicking a window captures it.
        _windowItem = Item("Capture window", "Icon.Window", () => CaptureDelayed(app, a => a.Capture.CaptureRegion()));
        _fullItem = Item("Capture full screen", "Icon.Monitor", () => CaptureDelayed(app, a => a.Capture.CaptureFullScreen()));
        _updateItem = Item("Check for Updates…", "Icon.Update", () => app.Updates.CheckNow());

        _menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            Items =
            {
                _regionItem,
                _windowItem,
                _fullItem,
                new Separator(),
                Item("Open captures folder", "Icon.Folder", app.OpenCapturesFolder),
                _updateItem,
                Item("Settings", "Icon.Settings", app.ShowSettings),
                new Separator(),
                Item("Exit Shottr", "Icon.Exit", app.ExitApp),
            },
        };

        // A ContextMenu needs a foreground window of ours to close properly when the user clicks away.
        _menuHost = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = true,
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
        };
        _menu.Closed += (_, _) => _menuHost.Hide();
    }

    /// <summary>Shows the hotkeys next to the menu items.</summary>
    public void UpdateHotkeyText(AppSettings settings)
    {
        _regionItem.InputGestureText = settings.RegionHotkey.IsEmpty ? "" : settings.RegionHotkey.ToString();
        _windowItem.InputGestureText = settings.WindowHotkey.IsEmpty ? "" : settings.WindowHotkey.ToString();
        _fullItem.InputGestureText = settings.FullScreenHotkey.IsEmpty ? "" : settings.FullScreenHotkey.ToString();
    }

    /// <summary>Shows a notification. When onClick is given, clicking the notification runs it.</summary>
    public void ShowBalloon(string title, string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.None);
    }

    /// <summary>Disables the update command while a check or download is running.</summary>
    public void SetUpdateBusy(bool busy)
    {
        _updateItem.IsEnabled = !busy;
        _updateItem.Header = busy ? "Checking for updates…" : "Check for Updates…";
    }

    /// <summary>Replaces the icon's tooltip, such as with download progress. Null restores it.</summary>
    public void SetStatusText(string? text)
    {
        // NotifyIcon tooltips are limited to 127 characters.
        string t = text ?? "Shottr";
        _icon.Text = t.Length > 127 ? t[..127] : t;
    }

    private void ShowMenu()
    {
        _menuHost.Show();
        _menuHost.Activate();
        _menu.PlacementTarget = _menuHost;
        _menu.IsOpen = true;
    }

    private static void CaptureDelayed(App app, Action<App> capture) =>
        Capture.CaptureService.Delayed(() => capture(app));

    private static MenuItem Item(string header, string iconKey, Action onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new Icon { Data = (Geometry)Application.Current.FindResource(iconKey), Size = 18 },
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menuHost.Close();
    }
}
