using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using Shottr.Interop;

namespace Shottr.Capture;

/// <summary>A visible top-level window and its on-screen bounds in physical pixels.</summary>
internal readonly record struct WindowInfo(IntPtr Handle, Int32Rect Bounds);

/// <summary>Lists the visible top-level windows in z-order so the region overlay can snap to them.</summary>
internal static class WindowFinder
{
    public static List<WindowInfo> GetVisibleWindows()
    {
        var result = new List<WindowInfo>();
        uint ownPid = (uint)Environment.ProcessId;
        IntPtr shell = Native.GetShellWindow();
        var cls = new StringBuilder(64);

        Native.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.IsCloaked(hwnd))
                return true;

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid)
                return true;

            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TRANSPARENT) != 0)
                return true;

            cls.Clear();
            Native.GetClassName(hwnd, cls, cls.Capacity);
            string name = cls.ToString();
            // The desktop itself is handled as "no window" so a click on it does nothing.
            if (name is "Progman" or "WorkerW")
                return true;

            // Tool windows without a title are usually invisible helpers (tooltips, shadows).
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && Native.GetWindowTextLength(hwnd) == 0 && name != "Shell_TrayWnd")
                return true;

            Native.RECT r = Native.GetVisibleBounds(hwnd);
            if (r.Width < 8 || r.Height < 8)
                return true;

            result.Add(new WindowInfo(hwnd, new Int32Rect(r.Left, r.Top, r.Width, r.Height)));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Returns the bounds of the current foreground window, or null if there is none.</summary>
    public static Int32Rect? GetForegroundWindowBounds()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == Native.GetShellWindow())
            return null;
        Native.RECT r = Native.GetVisibleBounds(hwnd);
        if (r.Width <= 0 || r.Height <= 0)
            return null;
        Debug.WriteLine($"Foreground window bounds: {r.Left},{r.Top} {r.Width}x{r.Height}");
        return new Int32Rect(r.Left, r.Top, r.Width, r.Height);
    }
}
