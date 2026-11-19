using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shottr.Core;

/// <summary>Writes finished images to disk and to the clipboard.</summary>
internal static class ImageExport
{
    public static void Save(BitmapSource image, string path, AppSettings settings)
    {
        BitmapEncoder encoder;
        BitmapSource frame = image;
        if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(settings.JpegQuality, 10, 100) };
            frame = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        }
        else
        {
            encoder = new PngBitmapEncoder();
        }
        encoder.Frames.Add(BitmapFrame.Create(frame));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>
    /// Puts the image on the clipboard both as a standard bitmap (for most apps) and as PNG
    /// (for apps such as browsers and chat clients that prefer it).
    /// </summary>
    public static void CopyToClipboard(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0));

        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);
        png.Position = 0;
        data.SetData("PNG", png, false);

        // Another app may briefly hold the clipboard open, so retry a few times.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (COMException) when (attempt < 10)
            {
                Thread.Sleep(40);
            }
        }
    }
}
