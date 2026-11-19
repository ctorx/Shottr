using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shottr.Editor.Annotations;

namespace Shottr.Editor;

/// <summary>
/// The drawing surface. It shows the screenshot with its annotations and handles all mouse
/// interaction. Its coordinate space is image pixels; the editor scales it for zoom and DPI.
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private enum DragMode
    {
        None,
        Create,
        Move,
        Resize,
    }

    private const int MaxUndo = 100;

    private readonly VisualCollection _children;
    private readonly DrawingVisual _background = new();
    private readonly DrawingVisual _selectionLayer = new();
    private readonly TextBox _editor;
    private readonly List<Annotation> _annotations = new();
    private readonly List<List<Annotation>> _undo = new();
    private readonly List<List<Annotation>> _redo = new();
    private readonly Brush _accent;

    private string? _lastUndoKey;
    private DateTime _lastUndoTime;

    private ToolKind _tool = ToolKind.Rectangle;
    private double _viewScale = 1;

    private DragMode _drag;
    private Point _dragStart;
    private Point _lastPoint;
    private int _handle;
    private bool _dragUndoPushed;

    private TextAnnotation? _editing;
    private bool _editIsNew;
    private string _editOriginalText = "";
    private bool _endingEdit;

    public AnnotationCanvas(BitmapSource image)
    {
        Image = image;
        Pixels = new PixelSource(image);
        Focusable = true;
        FocusVisualStyle = null;

        Color accent = SystemColors.AccentColor;
        _accent = new SolidColorBrush(accent);
        _accent.Freeze();

        _editor = CreateEditor();
        _children = new VisualCollection(this);
        RebuildChildren();
        AddLogicalChild(_editor);

        using (DrawingContext dc = _background.RenderOpen())
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
    }

    public BitmapSource Image { get; }

    public PixelSource Pixels { get; }

    /// <summary>Supplies the current options for a tool when a new annotation is created.</summary>
    public Func<ToolKind, ToolOptions> OptionsProvider { get; set; } = ToolOptions.DefaultFor;

    public ToolKind Tool
    {
        get => _tool;
        set
        {
            EndEdit();
            _tool = value;
            if (value != ToolKind.Select && Selected != null && Selected.Kind != value)
                Select(null);
            Cursor = ToolCursor;
        }
    }

    public Annotation? Selected { get; private set; }

    public bool IsEditingText => _editing != null;

    public bool HasAnnotations => _annotations.Count > 0;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Screen DIPs per image pixel. Used to keep handles a constant on-screen size.</summary>
    public double ViewScale
    {
        get => _viewScale;
        set
        {
            _viewScale = Math.Max(0.01, value);
            RenderSelection();
        }
    }

    /// <summary>Raised when the selected annotation changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised after any edit that changes the picture or the undo history.</summary>
    public event EventHandler? Changed;

    // ---- Visual tree ------------------------------------------------------------------------

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override IEnumerator LogicalChildren => new[] { _editor }.GetEnumerator();

    private void RebuildChildren()
    {
        _children.Clear();
        _children.Add(_background);
        foreach (Annotation a in _annotations)
            _children.Add(a.Visual);
        _children.Add(_selectionLayer);
        _children.Add(_editor);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _editor.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(Image.PixelWidth, Image.PixelHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_editing != null)
        {
            Rect body = _editing.BodyRect;
            Thickness pad = _editing.Padding;
            Size desired = _editor.DesiredSize;
            _editor.Arrange(new Rect(body.Left + pad.Left, body.Top + pad.Top,
                Math.Max(desired.Width, body.Width - pad.Left - pad.Right), Math.Max(desired.Height, body.Height - pad.Top - pad.Bottom)));
        }
        else
        {
            _editor.Arrange(new Rect(0, 0, 0, 0));
        }
        return new Size(Image.PixelWidth, Image.PixelHeight);
    }

    /// <summary>Uses crisp pixels when zoomed in and smooth scaling when zoomed out.</summary>
    public void SetZoomQuality(double screenPixelsPerImagePixel)
    {
        RenderOptions.SetBitmapScalingMode(this,
            screenPixelsPerImagePixel >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    // ---- Annotation list --------------------------------------------------------------------

    private void Add(Annotation a)
    {
        _annotations.Add(a);
        a.Refresh();
        _children.Insert(_children.Count - 2, a.Visual);
        Renumber();
    }

    private void Remove(Annotation a)
    {
        _annotations.Remove(a);
        _children.Remove(a.Visual);
        if (Selected == a)
            Select(null);
        Renumber();
    }

    /// <summary>Numbers the counter bubbles 1, 2, 3... in the order they were placed.</summary>
    private void Renumber()
    {
        int n = 1;
        foreach (CounterAnnotation c in _annotations.OfType<CounterAnnotation>())
        {
            if (c.Number != n)
            {
                c.Number = n;
                c.Refresh();
            }
            n++;
        }
    }

    public void Select(Annotation? a)
    {
        if (Selected == a)
            return;
        Selected = a;
        RenderSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelected()
    {
        if (Selected == null)
            return;
        PushUndo();
        Remove(Selected);
        RaiseChanged();
    }

    public void Nudge(Vector v)
    {
        if (Selected == null)
            return;
        PushUndo("nudge");
        Selected.Offset(v);
        Selected.Refresh();
        RenderSelection();
        RaiseChanged();
    }

    /// <summary>Applies an option change to the selected annotation. Returns false if nothing is selected.</summary>
    public bool ApplyToSelection(string optionName, Action<ToolOptions> change)
    {
        if (Selected == null)
            return false;
        PushUndo("option:" + optionName);
        change(Selected.Options);
        Selected.Refresh();
        if (_editing == Selected)
            StyleEditor(_editing);
        InvalidateArrange();
        RenderSelection();
        RaiseChanged();
        return true;
    }

    // ---- Undo -------------------------------------------------------------------------------

    /// <summary>
    /// Saves the current state for undo. Calls that share a key within a short time are merged,
    /// so dragging a slider produces one undo step instead of dozens.
    /// </summary>
    private void PushUndo(string? coalesceKey = null)
    {
        DateTime now = DateTime.UtcNow;
        if (coalesceKey != null && coalesceKey == _lastUndoKey && (now - _lastUndoTime).TotalSeconds < 1.5)
        {
            _lastUndoTime = now;
            return;
        }
        _lastUndoKey = coalesceKey;
        _lastUndoTime = now;
        _undo.Add(Snapshot());
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void DropLastUndo()
    {
        if (_undo.Count > 0)
            _undo.RemoveAt(_undo.Count - 1);
        _lastUndoKey = null;
    }

    private List<Annotation> Snapshot() => _annotations.Select(a => a.Clone()).ToList();

    public void Undo()
    {
        EndEdit();
        if (_undo.Count == 0)
            return;
        _redo.Add(Snapshot());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        _lastUndoKey = null;
        RaiseChanged();
    }

    public void Redo()
    {
        EndEdit();
        if (_redo.Count == 0)
            return;
        _undo.Add(Snapshot());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        _lastUndoKey = null;
        RaiseChanged();
    }

    private void Restore(List<Annotation> state)
    {
        Select(null);
        _annotations.Clear();
        _annotations.AddRange(state);
        foreach (Annotation a in _annotations)
            a.Refresh();
        RebuildChildren();
        Renumber();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ---- Mouse ------------------------------------------------------------------------------

    private double Tolerance => 5 / _viewScale;

    private double HandleSize => 10 / _viewScale;

    private Annotation? HitTestAll(Point p)
    {
        for (int i = _annotations.Count - 1; i >= 0; i--)
        {
            if (_annotations[i].HitTest(p, Tolerance))
                return _annotations[i];
        }
        return null;
    }

    private int HitHandle(Point p)
    {
        if (Selected == null)
            return -1;
        IReadOnlyList<Point> handles = Selected.Handles;
        double reach = HandleSize / 2 + 3 / _viewScale;
        for (int i = 0; i < handles.Count; i++)
        {
            if (Math.Abs(handles[i].X - p.X) <= reach && Math.Abs(handles[i].Y - p.Y) <= reach)
                return i;
        }
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point p = e.GetPosition(this);

        if (_editing != null)
        {
            // Clicking outside the text being edited finishes the edit.
            EndEdit();
            e.Handled = true;
            return;
        }

        Focus();
        _dragStart = _lastPoint = p;
        _dragUndoPushed = false;

        int handle = HitHandle(p);
        if (handle >= 0)
        {
            BeginDrag(DragMode.Resize, handle);
            e.Handled = true;
            return;
        }

        Annotation? hit = Selected != null && Selected.HitTest(p, Tolerance) ? Selected : HitTestAll(p);

        // Double-click text, or single-click it with the text tool, to edit it.
        if (hit is TextAnnotation text && (e.ClickCount == 2 || _tool == ToolKind.Text))
        {
            Select(text);
            BeginEdit(text, isNew: false);
            e.Handled = true;
            return;
        }

        if (hit != null)
        {
            Select(hit);
            BeginDrag(DragMode.Move, -1);
            e.Handled = true;
            return;
        }

        if (_tool == ToolKind.Select)
        {
            Select(null);
            e.Handled = true;
            return;
        }

        Annotation created = CreateAnnotation(_tool, p);
        PushUndo();
        Add(created);
        Select(created);

        if (created is TextAnnotation newText)
        {
            BeginEdit(newText, isNew: true);
        }
        else if (created is CounterAnnotation)
        {
            // The bubble is placed immediately; holding the button lets the user drag it into place.
            _dragUndoPushed = true;
            BeginDrag(DragMode.Move, -1);
            RaiseChanged();
        }
        else
        {
            BeginDrag(DragMode.Create, -1);
        }
        e.Handled = true;
    }

    private void BeginDrag(DragMode mode, int handle)
    {
        _drag = mode;
        _handle = handle;
        CaptureMouse();
        RenderSelection();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (_drag == DragMode.None || Selected == null)
        {
            UpdateHoverCursor(p);
            return;
        }

        if (_drag != DragMode.Create && !_dragUndoPushed)
        {
            if ((p - _dragStart).Length < 1)
                return;
            PushUndo();
            _dragUndoPushed = true;
        }

        switch (_drag)
        {
            case DragMode.Create:
                Selected.DragCreate(_dragStart, p, shift);
                break;
            case DragMode.Move:
                Selected.Offset(p - _lastPoint);
                break;
            case DragMode.Resize:
                Selected.MoveHandle(_handle, p, shift);
                break;
        }
        _lastPoint = p;
        Selected.Refresh();
        RenderSelection();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragMode.None)
            return;

        DragMode mode = _drag;
        _drag = DragMode.None;
        ReleaseMouseCapture();

        if (Selected != null)
        {
            if (mode == DragMode.Create && Selected.IsDegenerate)
            {
                // A click without a drag does not leave a zero-size shape behind.
                Remove(Selected);
                DropLastUndo();
            }
            else
            {
                if (Selected is BoxAnnotation box)
                    box.Normalize();
                Selected.Refresh();
                if (mode == DragMode.Create || _dragUndoPushed)
                    RaiseChanged();
            }
        }
        RenderSelection();
        UpdateHoverCursor(e.GetPosition(this));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragMode.None)
        {
            _drag = DragMode.None;
            RenderSelection();
        }
    }

    private Cursor ToolCursor => _tool switch
    {
        ToolKind.Select => Cursors.Arrow,
        ToolKind.Text => Cursors.IBeam,
        _ => Cursors.Cross,
    };

    private void UpdateHoverCursor(Point p)
    {
        int handle = HitHandle(p);
        if (handle >= 0)
        {
            Cursor = Selected is BoxAnnotation
                ? (handle % 4) switch
                {
                    0 => Cursors.SizeNWSE,
                    1 => Cursors.SizeNS,
                    2 => Cursors.SizeNESW,
                    _ => Cursors.SizeWE,
                }
                : Cursors.Hand;
            return;
        }
        Annotation? hit = HitTestAll(p);
        if (hit is TextAnnotation && _tool == ToolKind.Text)
            Cursor = Cursors.IBeam;
        else
            Cursor = hit != null ? Cursors.SizeAll : ToolCursor;
    }

    private Annotation CreateAnnotation(ToolKind kind, Point p)
    {
        ToolOptions o = OptionsProvider(kind).Clone();
        switch (kind)
        {
            case ToolKind.Ellipse:
                return new EllipseAnnotation(o) { Start = p, End = p };
            case ToolKind.Arrow:
                return new ArrowAnnotation(o) { Start = p, End = p };
            case ToolKind.Obfuscate:
                return new ObfuscateAnnotation(o, Pixels) { Start = p, End = p };
            case ToolKind.Counter:
                return new CounterAnnotation(o) { Center = p };
            case ToolKind.Text:
                var t = new TextAnnotation(o);
                Thickness pad = t.Padding;
                // Put the first line's middle at the click point.
                t.Position = new Point(p.X - pad.Left, p.Y - pad.Top - o.FontSize * 0.66);
                return t;
            default:
                return new RectangleAnnotation(o) { Start = p, End = p };
        }
    }

    // ---- Text editing -----------------------------------------------------------------------

    private TextBox CreateEditor()
    {
        // A bare template: no border, no padding, no scrolling. The text must line up exactly
        // with how TextAnnotation draws it once editing ends.
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(Border), "PART_ContentHost");
        host.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        template.VisualTree = host;

        var box = new TextBox
        {
            Template = template,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Visibility = Visibility.Collapsed,
            FocusVisualStyle = null,
            FontFamily = new FontFamily(TextAnnotation.FontFamilyName),
        };
        box.TextChanged += (_, _) =>
        {
            if (_editing == null)
                return;
            _editing.Text = box.Text;
            _editing.Refresh();
            InvalidateArrange();
            RenderSelection();
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                EndEdit();
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, e) =>
        {
            // Focus moving into a popup inside the options bar (like the color picker) keeps editing.
            if (e.NewFocus is DependencyObject d && IsInsideOptionsUi(d))
                return;
            EndEdit();
        };
        return box;
    }

    /// <summary>Set by the editor so focus moving into the options bar does not end text editing.</summary>
    public Func<DependencyObject, bool> IsInsideOptionsUi { get; set; } = _ => false;

    private void StyleEditor(TextAnnotation t)
    {
        _editor.FontSize = t.Options.FontSize;
        _editor.FontWeight = t.Typeface.Weight;
        var fg = new SolidColorBrush(t.Options.StrokeColor);
        _editor.Foreground = fg;
        _editor.CaretBrush = fg;
    }

    private void BeginEdit(TextAnnotation t, bool isNew)
    {
        EndEdit();
        _editing = t;
        _editIsNew = isNew;
        _editOriginalText = t.Text;
        if (!isNew)
            PushUndo();
        t.IsEditing = true;
        t.Refresh();

        StyleEditor(t);
        _editor.Text = t.Text;
        _editor.Visibility = Visibility.Visible;
        _editor.CaretIndex = _editor.Text.Length;
        InvalidateMeasure();
        InvalidateArrange();
        RenderSelection();
        Dispatcher.BeginInvoke(() => Keyboard.Focus(_editor), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Finishes text editing. An empty text block is removed.</summary>
    public void EndEdit()
    {
        if (_editing == null || _endingEdit)
            return;
        _endingEdit = true;
        try
        {
            TextAnnotation t = _editing;
            _editing = null;
            t.IsEditing = false;
            t.Text = _editor.Text.TrimEnd('\r', '\n');
            _editor.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(t.Text))
            {
                Remove(t);
                if (_editIsNew)
                    DropLastUndo();
                else
                    RaiseChanged();
            }
            else
            {
                t.Refresh();
                if (!_editIsNew && t.Text == _editOriginalText)
                    DropLastUndo();
                else
                    RaiseChanged();
            }
            InvalidateArrange();
            RenderSelection();
            Focus();
        }
        finally
        {
            _endingEdit = false;
        }
    }

    // ---- Selection rendering ----------------------------------------------------------------

    private void RenderSelection()
    {
        using DrawingContext dc = _selectionLayer.RenderOpen();
        Annotation? a = Selected;
        if (a == null || _drag == DragMode.Create)
            return;

        double s = 1 / _viewScale;
        var dashed = new Pen(_accent, 1.25 * s) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        var handlePen = new Pen(_accent, 1.5 * s);
        double hs = HandleSize;

        switch (a)
        {
            case ArrowAnnotation:
                foreach (Point p in a.Handles)
                    dc.DrawEllipse(Brushes.White, handlePen, p, hs / 2, hs / 2);
                break;
            case BoxAnnotation box:
                dc.DrawRectangle(null, dashed, box.Box);
                foreach (Point p in a.Handles)
                    dc.DrawRoundedRectangle(Brushes.White, handlePen, new Rect(p.X - hs / 2, p.Y - hs / 2, hs, hs), 2 * s, 2 * s);
                break;
            default:
                Rect r = a.Bounds;
                r.Inflate(4 * s, 4 * s);
                dc.DrawRoundedRectangle(null, dashed, r, 3 * s, 3 * s);
                break;
        }
    }

    // ---- Export -----------------------------------------------------------------------------

    /// <summary>Flattens the screenshot and all annotations into a single bitmap.</summary>
    public BitmapSource RenderImage()
    {
        EndEdit();
        int w = Image.PixelWidth, h = Image.PixelHeight;
        var root = new ContainerVisual { Clip = new RectangleGeometry(new Rect(0, 0, w, h)) };

        var bg = new DrawingVisual();
        using (DrawingContext dc = bg.RenderOpen())
            dc.DrawImage(Image, new Rect(0, 0, w, h));
        root.Children.Add(bg);

        foreach (Annotation a in _annotations)
        {
            Annotation copy = a.Clone();
            copy.Refresh();
            root.Children.Add(copy.Visual);
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }
}
