using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Shottr.Editor.Annotations;

/// <summary>
/// A block of text, optionally on a rounded label or a speech-bubble callout. The user places
/// the block first and then types or pastes text into it.
/// </summary>
public sealed class TextAnnotation : Annotation
{
    public const string FontFamilyName = "Segoe UI Variable Text, Segoe UI";
    private static readonly FontFamily Family = new(FontFamilyName);

    public TextAnnotation(ToolOptions options) : base(options)
    {
    }

    /// <summary>Top-left corner of the block.</summary>
    public Point Position { get; set; }

    public string Text { get; set; } = "";

    /// <summary>While true the canvas shows a live text box, so the text itself is not drawn.</summary>
    public bool IsEditing { get; set; }

    public override ToolKind Kind => ToolKind.Text;

    public Typeface Typeface => new(Family, FontStyles.Normal,
        Options.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);

    /// <summary>Space between the background edge and the text.</summary>
    public Thickness Padding
    {
        get
        {
            double fs = Options.FontSize;
            return Options.TextStyle == TextStyle.Plain
                ? new Thickness(2, 0, 2, 0)
                : new Thickness(fs * 0.5, fs * 0.25, fs * 0.5, fs * 0.25);
        }
    }

    private FormattedText Layout()
    {
        // Measure at least one character so an empty block still has a caret-sized box.
        string text = string.IsNullOrEmpty(Text) ? " " : Text;
        if (text.EndsWith('\n'))
            text += " ";
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface,
            Options.FontSize, new SolidColorBrush(Options.StrokeColor), 1.0)
        {
            Trimming = TextTrimming.None,
        };
    }

    /// <summary>The area covered by the label, without the callout tail.</summary>
    public Rect BodyRect
    {
        get
        {
            FormattedText ft = Layout();
            Thickness pad = Padding;
            double w = Math.Max(ft.WidthIncludingTrailingWhitespace, Options.FontSize * 0.6);
            return new Rect(Position.X, Position.Y, w + pad.Left + pad.Right, ft.Height + pad.Top + pad.Bottom);
        }
    }

    public override Rect Bounds
    {
        get
        {
            Rect r = BodyRect;
            if (Options.TextStyle == TextStyle.Callout)
                r.Height += Options.FontSize * 0.7;
            return r;
        }
    }

    protected override bool WantsShadow => Options.Shadow;

    protected override void Render(DrawingContext dc)
    {
        Rect body = BodyRect;
        Brush? bg = MakeBrush(Options.FillColor);
        double radius = Math.Min(Options.FontSize * 0.35, body.Height / 2);

        if (Options.TextStyle != TextStyle.Plain && bg != null)
        {
            Geometry shape = new RectangleGeometry(body, radius, radius);
            if (Options.TextStyle == TextStyle.Callout)
            {
                double fs = Options.FontSize;
                var tail = new PathFigure { StartPoint = new Point(body.Left + fs * 0.7, body.Bottom - 1), IsClosed = true };
                tail.Segments.Add(new LineSegment(new Point(body.Left + fs * 0.45, body.Bottom + fs * 0.7), true));
                tail.Segments.Add(new LineSegment(new Point(body.Left + fs * 1.5, body.Bottom - 1), true));
                shape = new CombinedGeometry(GeometryCombineMode.Union, shape, new PathGeometry(new[] { tail }));
            }
            dc.DrawGeometry(bg, null, shape);
        }

        if (!IsEditing && !string.IsNullOrEmpty(Text))
        {
            Thickness pad = Padding;
            dc.DrawText(Layout(), new Point(body.Left + pad.Left, body.Top + pad.Top));
        }
        else if (IsEditing && (Options.TextStyle == TextStyle.Plain || bg == null))
        {
            // Give a plain block a faint outline while typing so the user can see where it is.
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x80, 0x80, 0x80)), 1) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(Brushes.Transparent, pen, body);
        }
    }

    public override bool HitTest(Point p, double tolerance)
    {
        Rect r = Bounds;
        r.Inflate(tolerance, tolerance);
        return r.Contains(p);
    }

    public override void Offset(Vector v) => Position += v;

    protected override Annotation CreateCopy() => new TextAnnotation(Options) { Position = Position, Text = Text };
}
