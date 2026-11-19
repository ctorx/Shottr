using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Shottr.Editor.Annotations;

/// <summary>
/// Base class for everything drawn on top of a screenshot. Coordinates are in image pixels.
/// Each annotation renders into its own visual so that it can carry a drop shadow.
/// </summary>
public abstract class Annotation
{
    private static readonly DropShadowEffect ShadowEffect = CreateShadow();

    protected Annotation(ToolOptions options)
    {
        Options = options;
    }

    public ToolOptions Options { get; set; }

    public DrawingVisual Visual { get; } = new();

    public abstract ToolKind Kind { get; }

    /// <summary>The area the annotation covers, used for selection outlines and text placement.</summary>
    public abstract Rect Bounds { get; }

    protected virtual bool WantsShadow => Options.Shadow;

    /// <summary>Redraws the annotation into its visual. Call after any change.</summary>
    public void Refresh()
    {
        using (DrawingContext dc = Visual.RenderOpen())
            Render(dc);
        Visual.Effect = WantsShadow ? ShadowEffect : null;
    }

    protected abstract void Render(DrawingContext dc);

    /// <summary>True if the point touches the annotation. Tolerance is in image pixels.</summary>
    public abstract bool HitTest(Point p, double tolerance);

    /// <summary>Points the user can drag to reshape the annotation.</summary>
    public virtual IReadOnlyList<Point> Handles => Array.Empty<Point>();

    public virtual void MoveHandle(int index, Point p, bool constrain)
    {
    }

    /// <summary>Called while the annotation is first being dragged out.</summary>
    public virtual void DragCreate(Point start, Point current, bool constrain)
    {
    }

    /// <summary>True if the annotation is too small to keep after it was dragged out.</summary>
    public virtual bool IsDegenerate => false;

    public abstract void Offset(Vector v);

    /// <summary>Copies the annotation, including a private copy of its options.</summary>
    public Annotation Clone()
    {
        Annotation copy = CreateCopy();
        copy.Options = Options.Clone();
        return copy;
    }

    protected abstract Annotation CreateCopy();

    protected Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    protected static Brush? MakeBrush(Color color)
    {
        if (color.A == 0)
            return null;
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private static DropShadowEffect CreateShadow()
    {
        var e = new DropShadowEffect
        {
            Color = Colors.Black,
            Direction = 300,
            ShadowDepth = 2,
            BlurRadius = 8,
            Opacity = 0.4,
        };
        e.Freeze();
        return e;
    }
}
