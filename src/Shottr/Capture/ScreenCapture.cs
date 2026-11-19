using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shottr.Interop;

namespace Shottr.Capture;

/// <summary>Grabs pixels from the screen. All rectangles are in physical screen pixels.</summary>
internal static class ScreenCapture
{
    public static Int32Rect VirtualScreen => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    /// <summary>Returns the bounds of the monitor that currently holds the mouse cursor.</summary>
    public static Int32Rect MonitorUnderCursor()
    {
        Native.GetCursorPos(out Native.POINT pt);
        IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref info))
            return VirtualScreen;
        var r = info.rcMonitor;
        return new Int32Rect(r.Left, r.Top, r.Width, r.Height);
    }

    /// <summary>Clips a rectangle to the virtual screen. Returns an empty rect if nothing is left.</summary>
    public static Int32Rect ClipToVirtualScreen(Int32Rect r)
    {
        Int32Rect v = VirtualScreen;
        int left = Math.Max(r.X, v.X), top = Math.Max(r.Y, v.Y);
        int right = Math.Min(r.X + r.Width, v.X + v.Width), bottom = Math.Min(r.Y + r.Height, v.Y + v.Height);
        return right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : Int32Rect.Empty;
    }

    /// <summary>Copies the given screen area into a frozen, opaque BGR32 bitmap.</summary>
    public static BitmapSource Capture(Int32Rect area)
    {
        int w = area.Width, h = area.Height;
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hbmp = Native.CreateCompatibleBitmap(screenDc, w, h);
        IntPtr old = Native.SelectObject(memDc, hbmp);
        try
        {
            Native.BitBlt(memDc, 0, 0, w, h, screenDc, area.X, area.Y, Native.SRCCOPY | Native.CAPTUREBLT);
            Native.SelectObject(memDc, old);

            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // negative height = top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            int stride = w * 4;
            var pixels = new byte[stride * h];
            Native.GetDIBits(memDc, hbmp, 0, (uint)h, pixels, ref bmi, Native.DIB_RGB_COLORS);

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            Native.DeleteObject(hbmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Cuts a sub-rectangle out of an existing bitmap and returns it as a new frozen bitmap.</summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        int stride = rect.Width * 4;
        var pixels = new byte[stride * rect.Height];
        source.CopyPixels(rect, pixels, stride, 0);
        var bmp = BitmapSource.Create(rect.Width, rect.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
