using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shottr.Interop;

namespace Shottr.Capture;

/// <summary>
/// Full-screen overlay that shows a frozen copy of the desktop and lets the user drag out a
/// region or click a window. It covers the whole virtual screen and draws in physical pixels.
/// </summary>
internal sealed class RegionOverlay : Window
{
    private readonly BitmapSource _shot;
    private readonly Int32Rect _virtual;
    private readonly List<Rect> _windows = new();
    private readonly bool _showMagnifier;
    private readonly OverlaySurface _surface;

    private Point _mouse;
    private bool _hasMouse;
    private bool _dragging;
    private Point _dragStart;
    private Rect? _hoverWindow;
    private bool _finished;

    /// <summary>Raised with the cropped image when the user completes a selection.</summary>
    public event Action<BitmapSource>? Captured;

    public RegionOverlay(BitmapSource shot, Int32Rect virtualScreen, IEnumerable<WindowInfo> windows, bool showMagnifier)
    {
        _shot = shot;
        _virtual = virtualScreen;
        _showMagnifier = showMagnifier;
        foreach (WindowInfo w in windows)
        {
            var r = new Rect(w.Bounds.X - virtualScreen.X, w.Bounds.Y - virtualScreen.Y, w.Bounds.Width, w.Bounds.Height);
            r.Intersect(new Rect(0, 0, virtualScreen.Width, virtualScreen.Height));
            if (!r.IsEmpty && r.Width > 0 && r.Height > 0)
                _windows.Add(r);
        }

        Title = "Shottr capture";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        Cursor = Cursors.None;
        Left = virtualScreen.X;
        Top = virtualScreen.Y;
        Width = virtualScreen.Width;
        Height = virtualScreen.Height;

        _surface = new OverlaySurface(this);
        Content = _surface;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            Keyboard.Focus(this);
            Native.GetCursorPos(out Native.POINT p);
            _mouse = new Point(p.X - _virtual.X, p.Y - _virtual.Y);
            _hasMouse = true;
            UpdateHover();
            _surface.RenderAll();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        // Place the window in physical pixels so it covers every monitor exactly.
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, _virtual.X, _virtual.Y, _virtual.Width, _virtual.Height, 0);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Ignore DPI changes: the overlay draws in physical pixels and must not be resized.
        if (msg == Native.WM_DPICHANGED)
            handled = true;
        return IntPtr.Zero;
    }

    // ---- Input ------------------------------------------------------------------------------

    private Point ToPixels(MouseEventArgs e)
    {
        Point p = e.GetPosition(_surface);
        DpiScale dpi = VisualTreeHelper.GetDpi(_surface);
        return new Point(Math.Floor(p.X * dpi.DpiScaleX), Math.Floor(p.Y * dpi.DpiScaleY));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = ToPixels(e);
        _hasMouse = true;
        if (!_dragging)
            UpdateHover();
        _surface.RenderForeground();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _mouse = ToPixels(e);
        _dragStart = _mouse;
        _dragging = true;
        CaptureMouse();
        _surface.RenderForeground();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();
        _mouse = ToPixels(e);

        Rect sel = SelectionRect;
        if (sel.Width < 3 && sel.Height < 3)
        {
            // A plain click captures the window under the cursor.
            if (_hoverWindow is Rect win)
                Finish(win);
            else
                _surface.RenderForeground();
            return;
        }
        Finish(sel);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            UpdateHover();
            _surface.RenderForeground();
        }
        else
        {
            Cancel();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                break;
            case Key.Enter when _hoverWindow is Rect win && !_dragging:
                Finish(win);
                break;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // Losing focus to another app (for example via Alt+Tab) cancels the capture.
        if (!_finished)
            Cancel();
    }

    private void UpdateHover()
    {
        _hoverWindow = null;
        foreach (Rect r in _windows)
        {
            if (r.Contains(_mouse))
            {
                _hoverWindow = r;
                break;
            }
        }
    }

    private Rect SelectionRect
    {
        get
        {
            double x1 = Math.Min(_dragStart.X, _mouse.X), y1 = Math.Min(_dragStart.Y, _mouse.Y);
            double x2 = Math.Max(_dragStart.X, _mouse.X), y2 = Math.Max(_dragStart.Y, _mouse.Y);
            return new Rect(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
        }
    }

    private void Finish(Rect r)
    {
        r.Intersect(new Rect(0, 0, _shot.PixelWidth, _shot.PixelHeight));
        if (r.IsEmpty || r.Width < 1 || r.Height < 1)
        {
            Cancel();
            return;
        }
        _finished = true;
        var rect = new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        BitmapSource cropped = ScreenCapture.Crop(_shot, rect);
        Close();
        Captured?.Invoke(cropped);
    }

    private void Cancel()
    {
        if (_finished)
            return;
        _finished = true;
        Close();
    }

    // ---- Rendering --------------------------------------------------------------------------

    /// <summary>
    /// Holds two layers: the static screenshot and a foreground layer that redraws on every
    /// mouse move. Everything is drawn in physical pixels by undoing the window's DPI scale.
    /// </summary>
    private sealed class OverlaySurface : FrameworkElement
    {
        private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)));
        private static readonly Brush PillBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x22)));
        private static readonly Pen LightLine = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), 1));
        private static readonly Pen DarkLine = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), 1));
        private static readonly Typeface Font = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private static readonly Brush BorderBrush = Brushes.White;
        private static readonly Pen OutlinePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 1));

        private readonly RegionOverlay _o;
        private readonly DrawingVisual _background = new();
        private readonly DrawingVisual _foreground = new();

        public OverlaySurface(RegionOverlay owner)
        {
            _o = owner;
            AddVisualChild(_background);
            AddVisualChild(_foreground);
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }

        protected override int VisualChildrenCount => 2;
        protected override Visual GetVisualChild(int index) => index == 0 ? _background : _foreground;

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => RenderAll();

        private ScaleTransform InverseDpi
        {
            get
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                return new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
            }
        }

        private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        public void RenderAll()
        {
            using (DrawingContext dc = _background.RenderOpen())
            {
                dc.PushTransform(InverseDpi);
                dc.DrawImage(_o._shot, new Rect(0, 0, _o._shot.PixelWidth, _o._shot.PixelHeight));
                dc.Pop();
            }
            RenderForeground();
        }

        public void RenderForeground()
        {
            using DrawingContext dc = _foreground.RenderOpen();
            dc.PushTransform(InverseDpi);

            double w = _o._shot.PixelWidth, h = _o._shot.PixelHeight, s = Scale;
            Rect? sel = _o._dragging ? _o.SelectionRect : _o._hoverWindow;

            if (sel is Rect r)
            {
                // Dim everything except the selection.
                dc.DrawRectangle(Dim, null, new Rect(0, 0, w, r.Top));
                dc.DrawRectangle(Dim, null, new Rect(0, r.Bottom, w, Math.Max(0, h - r.Bottom)));
                dc.DrawRectangle(Dim, null, new Rect(0, r.Top, r.Left, r.Height));
                dc.DrawRectangle(Dim, null, new Rect(r.Right, r.Top, Math.Max(0, w - r.Right), r.Height));

                // A white border with a thin dark outline stays visible on any background,
                // whatever accent color Windows uses.
                double t = Math.Max(2, Math.Round(1.5 * s));
                Rect inner = new(r.X + t / 2, r.Y + t / 2, Math.Max(0, r.Width - t), Math.Max(0, r.Height - t));
                dc.DrawRectangle(null, OutlinePen, new Rect(r.X - 0.5, r.Y - 0.5, r.Width + 1, r.Height + 1));
                dc.DrawRectangle(null, new Pen(BorderBrush, t), inner);

                if (_o._dragging)
                    DrawHandles(dc, r, s);
                DrawSizePill(dc, r, s, w, h);
            }
            else
            {
                dc.DrawRectangle(Dim, null, new Rect(0, 0, w, h));
            }

            if (_o._hasMouse)
            {
                DrawCrosshair(dc, w, h);
                if (_o._showMagnifier)
                    DrawLoupe(dc, s, w, h);
            }
            dc.Pop();
        }

        private void DrawCrosshair(DrawingContext dc, double w, double h)
        {
            double x = _o._mouse.X + 0.5, y = _o._mouse.Y + 0.5;
            dc.DrawLine(DarkLine, new Point(x + 1, 0), new Point(x + 1, h));
            dc.DrawLine(DarkLine, new Point(0, y + 1), new Point(w, y + 1));
            dc.DrawLine(LightLine, new Point(x, 0), new Point(x, h));
            dc.DrawLine(LightLine, new Point(0, y), new Point(w, y));
        }

        private void DrawHandles(DrawingContext dc, Rect r, double s)
        {
            double size = Math.Round(6 * s);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Math.Max(1, Math.Round(1 * s)));
            foreach (Point p in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                dc.DrawEllipse(Brushes.White, pen, p, size / 2, size / 2);
        }

        private void DrawSizePill(DrawingContext dc, Rect r, double s, double w, double h)
        {
            string text = $"{(int)r.Width} × {(int)r.Height}";
            FormattedText ft = MakeText(text, 12 * s, Brushes.White);
            double padX = 8 * s, padY = 4 * s, gap = 8 * s;
            double pw = ft.Width + padX * 2, ph = ft.Height + padY * 2;

            // Prefer just below the selection's bottom-left corner, then above, then inside.
            double x = Math.Clamp(r.Left, 0, Math.Max(0, w - pw));
            double y = r.Bottom + gap;
            if (y + ph > h)
                y = r.Top - gap - ph;
            if (y < 0)
                y = r.Top + gap;

            var pill = new Rect(x, y, pw, ph);
            dc.DrawRoundedRectangle(PillBrush, null, pill, ph / 2, ph / 2);
            dc.DrawText(ft, new Point(x + padX, y + padY));
        }

        private void DrawLoupe(DrawingContext dc, double s, double w, double h)
        {
            const int cells = 15;
            double size = Math.Round(126 * s);
            double cell = size / cells;
            double offset = 22 * s;
            double mx = _o._mouse.X, my = _o._mouse.Y;

            FormattedText label = MakeText(
                string.Create(CultureInfo.InvariantCulture, $"{mx + _o._virtual.X}, {my + _o._virtual.Y}"),
                11 * s, Brushes.White);
            double labelH = label.Height + 8 * s;
            double totalH = size + 6 * s + labelH;

            double lx = mx + offset, ly = my + offset;
            if (lx + size > w) lx = mx - offset - size;
            if (ly + totalH > h) ly = my - offset - totalH;
            var box = new Rect(lx, ly, size, size);
            double radius = 10 * s;

            var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(box, radius, radius) };
            RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
            double ox = lx + (cells / 2) * cell - mx * cell;
            double oy = ly + (cells / 2) * cell - my * cell;
            group.Children.Add(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(box)));
            group.Children.Add(new ImageDrawing(_o._shot, new Rect(ox, oy, w * cell, h * cell)));
            dc.DrawDrawing(group);

            // Highlight the pixel under the cursor.
            var center = new Rect(lx + (cells / 2) * cell, ly + (cells / 2) * cell, cell, cell);
            dc.DrawRectangle(null, DarkLine, new Rect(center.X - 1, center.Y - 1, center.Width + 2, center.Height + 2));
            dc.DrawRectangle(null, LightLine, center);

            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Math.Max(1, s)),
                box, radius, radius);

            double lw = label.Width + 16 * s;
            var pill = new Rect(lx + (size - lw) / 2, ly + size + 6 * s, lw, labelH);
            dc.DrawRoundedRectangle(PillBrush, null, pill, labelH / 2, labelH / 2);
            dc.DrawText(label, new Point(pill.X + 8 * s, pill.Y + 4 * s));
        }

        private FormattedText MakeText(string text, double size, Brush brush) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Font, size, brush, 1.0);

        private static T Freeze<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }
    }
}
