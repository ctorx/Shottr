using System;
using System.Collections.Generic;
using System.Windows;

namespace Shottr.Editor.Annotations;

/// <summary>An annotation defined by a rectangle, with eight resize handles.</summary>
public abstract class BoxAnnotation : Annotation
{
    protected BoxAnnotation(ToolOptions options) : base(options)
    {
    }

    public Point Start { get; set; }
    public Point End { get; set; }

    public Rect Box => new(Start, End);

    public override Rect Bounds => Box;

    public override bool IsDegenerate => Box.Width < 3 && Box.Height < 3;

    // Handle order: top-left, top, top-right, right, bottom-right, bottom, bottom-left, left.
    public override IReadOnlyList<Point> Handles
    {
        get
        {
            Rect r = Box;
            double cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            return new[]
            {
                r.TopLeft, new Point(cx, r.Top), r.TopRight, new Point(r.Right, cy),
                r.BottomRight, new Point(cx, r.Bottom), r.BottomLeft, new Point(r.Left, cy),
            };
        }
    }

    public override void DragCreate(Point start, Point current, bool constrain)
    {
        Start = start;
        End = constrain ? Square(start, current) : current;
    }

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        Rect r = Box;
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        switch (index)
        {
            case 0: left = p.X; top = p.Y; break;
            case 1: top = p.Y; break;
            case 2: right = p.X; top = p.Y; break;
            case 3: right = p.X; break;
            case 4: right = p.X; bottom = p.Y; break;
            case 5: bottom = p.Y; break;
            case 6: left = p.X; bottom = p.Y; break;
            case 7: left = p.X; break;
        }

        if (constrain && index % 2 == 0)
        {
            // Keep the opposite corner fixed and force a square.
            Point anchor = index switch
            {
                0 => r.BottomRight,
                2 => r.BottomLeft,
                4 => r.TopLeft,
                _ => r.TopRight,
            };
            Point corner = Square(anchor, p);
            Start = anchor;
            End = corner;
            return;
        }

        Start = new Point(left, top);
        End = new Point(right, bottom);
    }

    /// <summary>Puts Start at the top-left and End at the bottom-right after a drag.</summary>
    public void Normalize()
    {
        Rect r = Box;
        Start = r.TopLeft;
        End = r.BottomRight;
    }

    public override void Offset(Vector v)
    {
        Start += v;
        End += v;
    }

    private static Point Square(Point anchor, Point p)
    {
        double dx = p.X - anchor.X, dy = p.Y - anchor.Y;
        double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point(anchor.X + Math.Sign(dx == 0 ? 1 : dx) * size, anchor.Y + Math.Sign(dy == 0 ? 1 : dy) * size);
    }
}
