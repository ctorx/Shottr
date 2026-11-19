using System;
using System.Windows;
using System.Windows.Media;

namespace Shottr.Editor.Annotations;

public sealed class RectangleAnnotation : BoxAnnotation
{
    public RectangleAnnotation(ToolOptions options) : base(options)
    {
    }

    public override ToolKind Kind => ToolKind.Rectangle;

    private Geometry Shape
    {
        get
        {
            double radius = Math.Min(Options.Thickness, 8);
            return new RectangleGeometry(Box, radius, radius);
        }
    }

    protected override void Render(DrawingContext dc)
    {
        dc.DrawGeometry(MakeBrush(Options.FillColor), MakePen(Options.StrokeColor, Options.Thickness), Shape);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        if (Options.FillColor.A > 0 && Box.Contains(p))
            return true;
        return Shape.StrokeContains(new Pen(Brushes.Black, Options.Thickness + tolerance * 2), p);
    }

    protected override Annotation CreateCopy() => new RectangleAnnotation(Options) { Start = Start, End = End };
}

public sealed class EllipseAnnotation : BoxAnnotation
{
    public EllipseAnnotation(ToolOptions options) : base(options)
    {
    }

    public override ToolKind Kind => ToolKind.Ellipse;

    private Geometry Shape => new EllipseGeometry(Box);

    protected override void Render(DrawingContext dc)
    {
        dc.DrawGeometry(MakeBrush(Options.FillColor), MakePen(Options.StrokeColor, Options.Thickness), Shape);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        if (Options.FillColor.A > 0 && Shape.FillContains(p))
            return true;
        return Shape.StrokeContains(new Pen(Brushes.Black, Options.Thickness + tolerance * 2), p);
    }

    protected override Annotation CreateCopy() => new EllipseAnnotation(Options) { Start = Start, End = End };
}

public sealed class ArrowAnnotation : Annotation
{
    public ArrowAnnotation(ToolOptions options) : base(options)
    {
    }

    public Point Start { get; set; }
    public Point End { get; set; }

    public override ToolKind Kind => ToolKind.Arrow;

    public override Rect Bounds => new(Start, End);

    public override bool IsDegenerate => (End - Start).Length < 4;

    public override System.Collections.Generic.IReadOnlyList<Point> Handles => new[] { Start, End };

    private double HeadLength => Math.Max(12, Options.Thickness * 3.4);

    public override void DragCreate(Point start, Point current, bool constrain)
    {
        Start = start;
        End = constrain ? SnapAngle(start, current) : current;
    }

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        if (index == 0)
            Start = constrain ? SnapAngle(End, p) : p;
        else
            End = constrain ? SnapAngle(Start, p) : p;
    }

    public override void Offset(Vector v)
    {
        Start += v;
        End += v;
    }

    protected override void Render(DrawingContext dc)
    {
        Vector d = End - Start;
        double len = d.Length;
        if (len < 0.5)
            return;
        d /= len;

        Color color = Options.StrokeColor;
        Brush fill = MakeBrush(color) ?? Brushes.Transparent;
        double head = Math.Min(HeadLength, len * (Options.ArrowHeads == ArrowHeads.Both ? 0.45 : 0.8));
        bool startHead = Options.ArrowHeads == ArrowHeads.Both;
        bool endHead = Options.ArrowHeads != ArrowHeads.None;

        // Stop the shaft inside each head so the round line cap does not poke through the tip.
        Point a = startHead ? Start + d * head * 0.7 : Start;
        Point b = endHead ? End - d * head * 0.7 : End;
        dc.DrawLine(MakePen(color, Options.Thickness), a, b);

        var headPen = MakePen(color, Math.Max(1, Options.Thickness * 0.35));
        if (endHead)
            dc.DrawGeometry(fill, headPen, Head(End, d, head));
        if (startHead)
            dc.DrawGeometry(fill, headPen, Head(Start, -d, head));
    }

    private Geometry Head(Point tip, Vector dir, double length)
    {
        var normal = new Vector(-dir.Y, dir.X);
        double halfWidth = length * 0.5;
        Point basePt = tip - dir * length;
        var fig = new PathFigure { StartPoint = tip, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(basePt + normal * halfWidth, true));
        fig.Segments.Add(new LineSegment(basePt - normal * halfWidth, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }

    public override bool HitTest(Point p, double tolerance)
    {
        double reach = Math.Max(Options.Thickness / 2, HeadLength / 3) + tolerance;
        return DistanceToSegment(p, Start, End) <= reach;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double lengthSq = ab.LengthSquared;
        if (lengthSq < 1e-6)
            return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSq, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>Snaps the direction from anchor to p to the nearest 15 degrees.</summary>
    private static Point SnapAngle(Point anchor, Point p)
    {
        Vector v = p - anchor;
        double angle = Math.Atan2(v.Y, v.X);
        double step = Math.PI / 12;
        angle = Math.Round(angle / step) * step;
        return anchor + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    protected override Annotation CreateCopy() => new ArrowAnnotation(Options) { Start = Start, End = End };
}
