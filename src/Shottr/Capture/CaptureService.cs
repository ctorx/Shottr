using System;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shottr.Core;
using Shottr.Editor;

namespace Shottr.Capture;

/// <summary>Runs the three capture modes and opens the result in the editor.</summary>
internal sealed class CaptureService
{
    private readonly AppSettings _settings;
    private RegionOverlay? _overlay;

    public CaptureService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Freezes the screen and lets the user drag a region or click a window.</summary>
    public void CaptureRegion()
    {
        if (_overlay != null)
        {
            _overlay.Activate();
            return;
        }
        var windows = WindowFinder.GetVisibleWindows();        Int32Rect screen = ScreenCapture.VirtualScreen;
        BitmapSource shot = ScreenCapture.Capture(screen);

        _overlay = new RegionOverlay(shot, screen, windows, _settings.ShowMagnifier);
        _overlay.Captured += OpenEditor;
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
    }

    /// <summary>Captures the window that currently has focus.</summary>
    public void CaptureActiveWindow()
    {
        Int32Rect? bounds = WindowFinder.GetForegroundWindowBounds();
        if (bounds is not Int32Rect b)
        {
            CaptureFullScreen();
            return;
        }
        Int32Rect clipped = ScreenCapture.ClipToVirtualScreen(b);
        if (clipped.IsEmpty)
            return;
        OpenEditor(ScreenCapture.Capture(clipped));
    }

    /// <summary>Captures the monitor under the cursor, or every monitor, depending on settings.</summary>
    public void CaptureFullScreen()
    {
        Int32Rect area = _settings.FullScreen == FullScreenMode.AllMonitors
            ? ScreenCapture.VirtualScreen
            : ScreenCapture.MonitorUnderCursor();
        OpenEditor(ScreenCapture.Capture(area));
    }

    /// <summary>Runs a capture after a short pause, so a just-closed menu is gone from the screen.</summary>
    public static void Delayed(Action capture)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            capture();
        };
        timer.Start();
    }

    /// <summary>Opens an existing image file in the editor.</summary>
    public void OpenFile(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(System.IO.Path.GetFullPath(path));
        bmp.EndInit();
        // Normalize to 96 DPI so one image pixel is one canvas unit.
        int stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgr32, null, 0).CopyPixels(pixels, stride, 0);
        var image = BitmapSource.Create(bmp.PixelWidth, bmp.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
        image.Freeze();
        OpenEditor(image);
    }

    private void OpenEditor(BitmapSource image)
    {
        var editor = new EditorWindow(image, _settings);
        editor.Show();
        editor.Activate();
    }
}
