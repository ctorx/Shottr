// Renders the Shottr app icon at every size Windows asks for and packs the
// results into a multi-resolution .ico plus a 512px preview PNG.
//
// Usage: dotnet run --project tools/IconGen -- <output folder>
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

internal static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    [STAThread]
    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);

        var frames = new List<(int Size, BitmapSource Bitmap)>();
        foreach (int size in Sizes)
            frames.Add((size, Render(size)));

        WriteIco(Path.Combine(outDir, "shottr.ico"), frames);
        WritePng(Path.Combine(outDir, "shottr-512.png"), Render(512));
        WritePng(Path.Combine(outDir, "shottr-256.png"), Render(256));
        Console.WriteLine($"Wrote icon assets to {Path.GetFullPath(outDir)}");
        return 0;
    }

    /// <summary>Draws the icon on a 256-unit grid scaled to the requested pixel size.</summary>
    private static BitmapSource Render(int size)
    {
        double u = size / 256.0;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // Rounded-square tile with a diagonal indigo-to-violet gradient.
            var bg = new LinearGradientBrush(
                Color.FromRgb(0x4F, 0x5B, 0xF7), Color.FromRgb(0xA3, 0x3D, 0xF2),
                new Point(0, 0), new Point(1, 1));
            double inset = size <= 24 ? 0 : 8 * u;
            var tile = new Rect(inset, inset, size - inset * 2, size - inset * 2);
            double radius = 58 * u;
            dc.DrawRoundedRectangle(bg, null, tile, radius, radius);

            // Soft highlight across the top half gives the tile some depth.
            var sheen = new LinearGradientBrush(
                Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                new Point(0.5, 0), new Point(0.5, 0.6));
            dc.DrawRoundedRectangle(sheen, null, tile, radius, radius);

            // Viewfinder corner brackets.
            double stroke = Math.Max(1.7, 20 * u);
            var pen = new Pen(Brushes.White, stroke)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            double a = 62 * u, b = 194 * u, arm = 44 * u, r = 22 * u;
            dc.DrawGeometry(null, pen, Corner(a, a + arm, a, a + arm, r, 0));
            dc.DrawGeometry(null, pen, Corner(b, b - arm, a, a + arm, r, 1));
            dc.DrawGeometry(null, pen, Corner(b, b - arm, b, b - arm, r, 2));
            dc.DrawGeometry(null, pen, Corner(a, a + arm, b, b - arm, r, 3));

            // The "shot": a solid dot in the middle of the frame.
            double dot = Math.Max(1.6, 30 * u);
            dc.DrawEllipse(Brushes.White, null, new Point(128 * u, 128 * u), dot, dot);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Builds one L-shaped bracket. (cx, cy) is the corner, the arms run to armX and armY.
    /// </summary>
    private static Geometry Corner(double cx, double armX, double cy, double armY, double r, int quadrant)
    {
        double sx = Math.Sign(armX - cx), sy = Math.Sign(armY - cy);
        var fig = new PathFigure { StartPoint = new Point(cx, armY), IsClosed = false };
        fig.Segments.Add(new LineSegment(new Point(cx, cy + sy * r), true));
        bool sweep = quadrant is 0 or 2;
        fig.Segments.Add(new ArcSegment(new Point(cx + sx * r, cy), new Size(r, r), 0, false,
            sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
        fig.Segments.Add(new LineSegment(new Point(armX, cy), true));
        return new PathGeometry(new[] { fig });
    }

    private static void WritePng(string path, BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>
    /// Writes an .ico file. Frames under 256px are stored as 32-bit DIBs so that
    /// every consumer (including System.Drawing) can read them. The 256px frame is PNG.
    /// </summary>
    private static void WriteIco(string path, List<(int Size, BitmapSource Bitmap)> frames)
    {
        var images = new List<byte[]>();
        foreach (var (size, bmp) in frames)
            images.Add(size >= 256 ? EncodePng(bmp) : EncodeDib(bmp, size));

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)frames.Count);

        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int size = frames[i].Size;
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (byte[] img in images)
            w.Write(img);
    }

    private static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] EncodeDib(BitmapSource bmp, int size)
    {
        // Icons want straight (non-premultiplied) BGRA, stored bottom-up.
        var straight = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = size * 4;
        var pixels = new byte[stride * size];
        straight.CopyPixels(pixels, stride, 0);

        int maskStride = ((size + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);
        w.Write(size);
        w.Write(size * 2);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(0);
        w.Write(stride * size + maskStride * size);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        for (int y = size - 1; y >= 0; y--)
            w.Write(pixels, y * stride, stride);
        w.Write(new byte[maskStride * size]);
        return ms.ToArray();
    }
}
