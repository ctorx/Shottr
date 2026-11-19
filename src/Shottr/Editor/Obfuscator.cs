using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shottr.Editor;

/// <summary>A read-only copy of the screenshot's pixels, used as the source for obfuscation.</summary>
public sealed class PixelSource
{
    public PixelSource(BitmapSource bmp)
    {
        BitmapSource src = bmp.Format == PixelFormats.Bgr32 || bmp.Format == PixelFormats.Bgra32 || bmp.Format == PixelFormats.Pbgra32
            ? bmp
            : new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
        Width = src.PixelWidth;
        Height = src.PixelHeight;
        Stride = Width * 4;
        Pixels = new byte[Stride * Height];
        src.CopyPixels(Pixels, Stride, 0);
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
}

/// <summary>Pixelates or blurs a region of the original screenshot.</summary>
public static class Obfuscator
{
    /// <summary>Maps the 1-100 strength slider to a pixel block size.</summary>
    public static int BlockSize(double strength) => (int)Math.Round(3 + Math.Clamp(strength, 1, 100) / 100 * 37);

    /// <summary>Maps the 1-100 strength slider to a blur radius.</summary>
    public static int BlurRadius(double strength) => (int)Math.Round(1 + Math.Clamp(strength, 1, 100) / 100 * 24);

    public static BitmapSource Apply(PixelSource src, Int32Rect r, ObfuscateMode mode, double strength)
    {
        byte[] output = mode == ObfuscateMode.Blur
            ? Blur(src, r, BlurRadius(strength))
            : Pixelate(src, r, BlockSize(strength));
        var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgr32, null, output, r.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] Pixelate(PixelSource src, Int32Rect r, int block)
    {
        int w = r.Width, h = r.Height, outStride = w * 4;
        var output = new byte[outStride * h];
        byte[] px = src.Pixels;

        // Blocks are aligned to the image grid so they stay put while the region is resized.
        int startX = r.X / block * block, startY = r.Y / block * block;
        for (int by = startY; by < r.Y + h; by += block)
        {
            int y0 = Math.Max(by, r.Y), y1 = Math.Min(by + block, r.Y + h);
            for (int bx = startX; bx < r.X + w; bx += block)
            {
                int x0 = Math.Max(bx, r.X), x1 = Math.Min(bx + block, r.X + w);
                long sb = 0, sg = 0, sr = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = y * src.Stride + x0 * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        sb += px[i];
                        sg += px[i + 1];
                        sr += px[i + 2];
                        count++;
                    }
                }
                if (count == 0)
                    continue;
                byte b = (byte)(sb / count), g = (byte)(sg / count), rr = (byte)(sr / count);
                for (int y = y0; y < y1; y++)
                {
                    int o = (y - r.Y) * outStride + (x0 - r.X) * 4;
                    for (int x = x0; x < x1; x++, o += 4)
                    {
                        output[o] = b;
                        output[o + 1] = g;
                        output[o + 2] = rr;
                        output[o + 3] = 255;
                    }
                }
            }
        }
        return output;
    }

    private static byte[] Blur(PixelSource src, Int32Rect r, int radius)
    {
        // Blur a margin around the region too, so the edges blend with real neighbors.
        int margin = radius * 3;
        int ax = Math.Max(0, r.X - margin), ay = Math.Max(0, r.Y - margin);
        int aw = Math.Min(src.Width, r.X + r.Width + margin) - ax;
        int ah = Math.Min(src.Height, r.Y + r.Height + margin) - ay;

        int n = aw * ah;
        var ch = new float[3][] { new float[n], new float[n], new float[n] };
        for (int y = 0; y < ah; y++)
        {
            int i = (ay + y) * src.Stride + ax * 4;
            int o = y * aw;
            for (int x = 0; x < aw; x++, i += 4, o++)
            {
                ch[0][o] = src.Pixels[i];
                ch[1][o] = src.Pixels[i + 1];
                ch[2][o] = src.Pixels[i + 2];
            }
        }

        // Three box blur passes approximate a Gaussian.
        var tmp = new float[n];
        foreach (float[] c in ch)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                BoxHorizontal(c, tmp, aw, ah, radius);
                BoxVertical(tmp, c, aw, ah, radius);
            }
        }

        int outStride = r.Width * 4;
        var output = new byte[outStride * r.Height];
        for (int y = 0; y < r.Height; y++)
        {
            int si = (r.Y - ay + y) * aw + (r.X - ax);
            int o = y * outStride;
            for (int x = 0; x < r.Width; x++, si++, o += 4)
            {
                output[o] = (byte)Math.Clamp(ch[0][si], 0, 255);
                output[o + 1] = (byte)Math.Clamp(ch[1][si], 0, 255);
                output[o + 2] = (byte)Math.Clamp(ch[2][si], 0, 255);
                output[o + 3] = 255;
            }
        }
        return output;
    }

    private static void BoxHorizontal(float[] src, float[] dst, int w, int h, int r)
    {
        float scale = 1f / (r * 2 + 1);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0;
            for (int k = -r; k <= r; k++)
                sum += src[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = sum * scale;
                sum += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
            }
        }
    }

    private static void BoxVertical(float[] src, float[] dst, int w, int h, int r)
    {
        float scale = 1f / (r * 2 + 1);
        for (int x = 0; x < w; x++)
        {
            float sum = 0;
            for (int k = -r; k <= r; k++)
                sum += src[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = sum * scale;
                sum += src[Math.Min(y + r + 1, h - 1) * w + x] - src[Math.Max(y - r, 0) * w + x];
            }
        }
    }
}
