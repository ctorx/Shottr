using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shottr.Editor.Annotations;

/// <summary>Hides part of the screenshot by pixelating or blurring the original pixels underneath.</summary>
public sealed class ObfuscateAnnotation : BoxAnnotation
{
    private readonly PixelSource _source;
    private BitmapSource? _cache;
    private Int32Rect _cacheRect;
    private ObfuscateMode _cacheMode;
    private double _cacheStrength;

    public ObfuscateAnnotation(ToolOptions options, PixelSource source) : base(options)
    {
        _source = source;
    }

    public override ToolKind Kind => ToolKind.Obfuscate;

    protected override bool WantsShadow => false;

    /// <summary>The box snapped to whole pixels and clipped to the image.</summary>
    private Int32Rect PixelRect
    {
        get
        {
            Rect b = Box;
            int x0 = (int)Math.Max(0, Math.Floor(b.Left)), y0 = (int)Math.Max(0, Math.Floor(b.Top));
            int x1 = (int)Math.Min(_source.Width, Math.Ceiling(b.Right));
            int y1 = (int)Math.Min(_source.Height, Math.Ceiling(b.Bottom));
            return x1 > x0 && y1 > y0 ? new Int32Rect(x0, y0, x1 - x0, y1 - y0) : Int32Rect.Empty;
        }
    }

    protected override void Render(DrawingContext dc)
    {
        Int32Rect r = PixelRect;
        if (r.IsEmpty)
            return;
        if (_cache == null || _cacheRect != r || _cacheMode != Options.ObfuscateMode || _cacheStrength != Options.Strength)
        {
            _cache = Obfuscator.Apply(_source, r, Options.ObfuscateMode, Options.Strength);
            _cacheRect = r;
            _cacheMode = Options.ObfuscateMode;
            _cacheStrength = Options.Strength;
        }
        dc.DrawImage(_cache, new Rect(r.X, r.Y, r.Width, r.Height));
    }

    public override bool HitTest(Point p, double tolerance)
    {
        Rect r = Box;
        r.Inflate(tolerance, tolerance);
        return r.Contains(p);
    }

    protected override Annotation CreateCopy() =>
        new ObfuscateAnnotation(Options, _source) { Start = Start, End = End, _cache = _cache, _cacheRect = _cacheRect, _cacheMode = _cacheMode, _cacheStrength = _cacheStrength };
}
