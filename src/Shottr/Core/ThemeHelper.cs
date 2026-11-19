using System;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Shottr.Interop;

namespace Shottr.Core;

/// <summary>Reads the Windows light/dark setting and keeps window title bars in step with it.</summary>
internal static class ThemeHelper
{
    public static bool IsDarkMode
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
    }

    /// <summary>Re-applies the title bar color to every open window when the Windows theme changes.</summary>
    public static void WatchThemeChanges(Application app)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General)
                return;
            app.Dispatcher.BeginInvoke(() =>
            {
                bool dark = IsDarkMode;
                foreach (Window w in app.Windows)
                {
                    IntPtr hwnd = new WindowInteropHelper(w).Handle;
                    if (hwnd != IntPtr.Zero)
                        Native.SetDarkTitleBar(hwnd, dark);
                }
            });
        };
    }
}
