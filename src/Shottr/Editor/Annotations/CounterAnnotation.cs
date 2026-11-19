using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Shottr.Editor.Annotations;

/// <summary>
/// A numbered bubble. The canvas assigns the numbers in creation order, so deleting one
/// renumbers the rest.
/// </summary>
public sealed class CounterAnnotation : Annotation
{
    private static readonly Typeface Font = new(new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public CounterAnnotation(ToolOptions options) : base(options)
    {
    }

    public Point Center { get; set; }

    public int Number { get; set; } = 1;

    public override ToolKind Kind => ToolKind.Counter;

    private double Radius => Options.CounterSize / 2;

    public override Rect Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    protected override void Render(DrawingContext dc)
    {
        Color fill = Options.FillColor.A == 0 ? Palette.Parse(Palette.Red) : Options.FillColor;
        double r = Radius;
        double ring = System.Math.Max(1.5, r * 0.09);
        dc.DrawEllipse(MakeBrush(fill), MakePen(Colors.White, ring), Center, r - ring / 2, r - ring / 2);

        string text = Number.ToString(CultureInfo.InvariantCulture);
        double size = r * (text.Length > 2 ? 0.8 : text.Length > 1 ? 1.0 : 1.18);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size,
            new SolidColorBrush(Palette.ContrastText(fill)), 1.0);
        // Center on the glyph box rather than the line box so digits sit visually centered.
        double top = ft.Height + ft.OverhangAfter - ft.Extent;
        dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - top - ft.Extent / 2));
    }

    public override bool HitTest(Point p, double tolerance) => (p - Center).Length <= Radius + tolerance;

    public override void Offset(Vector v) => Center += v;

    protected override Annotation CreateCopy() => new CounterAnnotation(Options) { Center = Center, Number = Number };
}
