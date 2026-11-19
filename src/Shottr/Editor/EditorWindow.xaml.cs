using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Shottr.Core;

namespace Shottr.Editor;

/// <summary>
/// The annotation editor. Every capture opens straight into one of these windows.
/// </summary>
public partial class EditorWindow : Window
{
    private static readonly double[] ZoomSteps = { 0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8 };

    private readonly AppSettings _settings;
    private readonly AnnotationCanvas _canvas;
    private readonly ScaleTransform _zoomTransform = new();
    private readonly Dictionary<ToolKind, RadioButton> _toolButtons;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };

    private double _zoom = 1;
    private bool _fitMode = true;
    private bool _syncingOptions;
    private string? _lastSavedPath;

    public EditorWindow(BitmapSource image, AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        _canvas = new AnnotationCanvas(image)
        {
            OptionsProvider = kind => _settings.GetToolOptions(kind),
            IsInsideOptionsUi = IsInsideOptionsUi,
        };
        CanvasShadow.Width = image.PixelWidth;
        CanvasShadow.Height = image.PixelHeight;
        CanvasHost.Children.Add(_canvas);
        CanvasHost.LayoutTransform = _zoomTransform;

        _canvas.SelectionChanged += (_, _) => RefreshOptionsBar();
        _canvas.Changed += (_, _) => UpdateCommandStates();

        _toolButtons = new Dictionary<ToolKind, RadioButton>
        {
            [ToolKind.Select] = ToolSelect,
            [ToolKind.Rectangle] = ToolRectangle,
            [ToolKind.Ellipse] = ToolEllipse,
            [ToolKind.Arrow] = ToolArrow,
            [ToolKind.Text] = ToolText,
            [ToolKind.Counter] = ToolCounter,
            [ToolKind.Obfuscate] = ToolObfuscate,
        };
        foreach (var (kind, button) in _toolButtons)
        {
            ToolKind k = kind;
            button.Checked += (_, _) => SetTool(k);
        }

        WireOptionControls();

        Title = $"Shottr  ·  {image.PixelWidth} × {image.PixelHeight}";
        SizeText.Text = $"{image.PixelWidth} × {image.PixelHeight} px";
        _toastTimer.Tick += (_, _) => HideToast();

        SourceInitialized += (_, _) => SizeToImage();
        Loaded += (_, _) =>
        {
            ApplyZoom();
            _canvas.Focus();
        };
        Scroller.SizeChanged += (_, _) =>
        {
            if (_fitMode)
                ApplyZoom();
        };
        DpiChanged += (_, _) => ApplyZoom();
        Closed += (_, _) => _settings.Save();

        SetTool(ToolKind.Rectangle);
        UpdateCommandStates();
    }

    // ---- Window sizing and zoom -------------------------------------------------------------

    /// <summary>Sizes the window to show the whole image when it fits on the current monitor.</summary>
    private void SizeToImage()
    {
        // Work in physical pixels on the monitor under the mouse, then center the window there.
        // WPF's own CenterScreen runs before this resize, so it cannot be relied on.
        Interop.Native.GetCursorPos(out Interop.Native.POINT pt);
        IntPtr monitor = Interop.Native.MonitorFromPoint(pt, Interop.Native.MONITOR_DEFAULTTONEAREST);
        var info = new Interop.Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Interop.Native.MONITORINFO>() };
        if (!Interop.Native.GetMonitorInfo(monitor, ref info))
            return;
        Interop.Native.RECT work = info.rcWork;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        // Room taken by the toolbar, header, options bar, status bar, margins and the window frame, in DIPs.
        const double chromeW = 64 + 64 + 20, chromeH = 61 + 57 + 41 + 64 + 40;
        int w = (int)Math.Clamp(_canvas.Image.PixelWidth + chromeW * dpi.DpiScaleX, MinWidth * dpi.DpiScaleX, work.Width * 0.9);
        int h = (int)Math.Clamp(_canvas.Image.PixelHeight + chromeH * dpi.DpiScaleY, MinHeight * dpi.DpiScaleY, work.Height * 0.9);
        int x = work.Left + (work.Width - w) / 2, y = work.Top + (work.Height - h) / 2;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Interop.Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Interop.Native.SWP_NOZORDER | Interop.Native.SWP_NOACTIVATE);
    }

    private double DpiScaleX => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private double FitZoom()
    {
        double availW = Math.Max(50, Scroller.ActualWidth - 64 - 4);
        double availH = Math.Max(50, Scroller.ActualHeight - 64 - 4);
        double dpi = DpiScaleX;
        double z = Math.Min(availW * dpi / _canvas.Image.PixelWidth, availH * dpi / _canvas.Image.PixelHeight);
        return Math.Min(1, z);
    }

    private void ApplyZoom()
    {
        if (_fitMode && Scroller.ActualWidth > 0)
            _zoom = FitZoom();
        double s = _zoom / DpiScaleX; // DIPs per image pixel, so 100% means one screen pixel per image pixel
        _zoomTransform.ScaleX = _zoomTransform.ScaleY = s;
        _canvas.ViewScale = s;
        _canvas.SetZoomQuality(_zoom);
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void ZoomBy(int direction)
    {
        _fitMode = false;
        double next = _zoom;
        if (direction > 0)
        {
            foreach (double step in ZoomSteps)
            {
                if (step > _zoom + 0.001) { next = step; break; }
            }
        }
        else
        {
            for (int i = ZoomSteps.Length - 1; i >= 0; i--)
            {
                if (ZoomSteps[i] < _zoom - 0.001) { next = ZoomSteps[i]; break; }
            }
        }
        _zoom = next;
        ApplyZoom();
    }

    private void SetZoom(double zoom, bool fit)
    {
        _fitMode = fit;
        _zoom = zoom;
        ApplyZoom();
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        ZoomBy(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(-1);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => SetZoom(1, fit: true);
    private void ZoomActual_Click(object sender, RoutedEventArgs e) => SetZoom(1, fit: false);

    // ---- Tools and the options bar ----------------------------------------------------------

    private void SetTool(ToolKind kind)
    {
        if (_toolButtons[kind].IsChecked != true)
            _toolButtons[kind].IsChecked = true; // re-enters through the Checked handler
        _canvas.Tool = kind;
        RefreshOptionsBar();
        _canvas.Focus();
    }

    /// <summary>The tool whose options the bar is showing: the selected markup's, or the active tool's.</summary>
    private ToolKind ActiveKind => _canvas.Selected?.Kind ?? _canvas.Tool;

    private ToolOptions ActiveOptions => _canvas.Selected?.Options ?? _settings.GetToolOptions(ActiveKind);

    private static string ToolName(ToolKind kind) => kind switch
    {
        ToolKind.Select => "Select",
        ToolKind.Rectangle => "Rectangle",
        ToolKind.Ellipse => "Ellipse",
        ToolKind.Arrow => "Arrow",
        ToolKind.Text => "Text",
        ToolKind.Counter => "Counter",
        ToolKind.Obfuscate => "Obfuscate",
        _ => kind.ToString(),
    };

    /// <summary>Shows the option groups that apply to the active tool and loads their values.</summary>
    private void RefreshOptionsBar()
    {
        ToolKind kind = ActiveKind;
        ToolOptions o = ActiveOptions;
        _syncingOptions = true;
        try
        {
            ToolTitle.Text = ToolName(kind);

            bool shape = kind is ToolKind.Rectangle or ToolKind.Ellipse;
            Show(StrokeGroup, shape || kind is ToolKind.Arrow or ToolKind.Text);
            Show(FillGroup, shape || kind is ToolKind.Text or ToolKind.Counter);
            Show(ThicknessGroup, shape || kind == ToolKind.Arrow);
            Show(FontSizeGroup, kind == ToolKind.Text);
            Show(BoldGroup, kind == ToolKind.Text);
            Show(TextStyleGroup, kind == ToolKind.Text);
            Show(CounterSizeGroup, kind == ToolKind.Counter);
            Show(ModeGroup, kind == ToolKind.Obfuscate);
            Show(StrengthGroup, kind == ToolKind.Obfuscate);
            Show(ArrowHeadsGroup, kind == ToolKind.Arrow);
            Show(ShadowGroup, kind is not (ToolKind.Select or ToolKind.Obfuscate));
            Show(SelectHint, kind == ToolKind.Select);

            StrokeLabel.Text = kind == ToolKind.Text ? "Text" : "Color";
            FillLabel.Text = kind switch
            {
                ToolKind.Text => "Background",
                ToolKind.Counter => "Color",
                _ => "Fill",
            };
            FillPicker.AllowTransparent = kind != ToolKind.Counter;
            if (kind == ToolKind.Text && o.TextStyle == TextStyle.Plain)
                Show(FillGroup, false);

            StrokePicker.SelectedColor = o.StrokeColor;
            FillPicker.SelectedColor = o.FillColor;
            ThicknessSlider.Value = o.Thickness;
            ThicknessValue.Text = $"{o.Thickness:0} px";
            FontSizeSlider.Value = o.FontSize;
            FontSizeValue.Text = $"{o.FontSize:0} px";
            BoldToggle.IsChecked = o.Bold;
            StylePlain.IsChecked = o.TextStyle == TextStyle.Plain;
            StyleLabel.IsChecked = o.TextStyle == TextStyle.Label;
            StyleCallout.IsChecked = o.TextStyle == TextStyle.Callout;
            CounterSizeSlider.Value = o.CounterSize;
            CounterSizeValue.Text = $"{o.CounterSize:0} px";
            ModePixelate.IsChecked = o.ObfuscateMode == ObfuscateMode.Pixelate;
            ModeBlur.IsChecked = o.ObfuscateMode == ObfuscateMode.Blur;
            StrengthSlider.Value = o.Strength;
            StrengthValue.Text = $"{o.Strength:0}%";
            HeadsEnd.IsChecked = o.ArrowHeads == ArrowHeads.End;
            HeadsBoth.IsChecked = o.ArrowHeads == ArrowHeads.Both;
            HeadsNone.IsChecked = o.ArrowHeads == ArrowHeads.None;
            ShadowCheck.IsChecked = o.Shadow;
        }
        finally
        {
            _syncingOptions = false;
        }
        UpdateCommandStates();
    }

    private static void Show(UIElement e, bool visible) => e.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void WireOptionControls()
    {
        StrokePicker.ColorPicked += (_, _) => ChangeOption("stroke", o => o.StrokeColor = StrokePicker.SelectedColor);
        FillPicker.ColorPicked += (_, _) => ChangeOption("fill", o => o.FillColor = FillPicker.SelectedColor);

        ThicknessSlider.ValueChanged += (_, e) =>
        {
            ThicknessValue.Text = $"{e.NewValue:0} px";
            ChangeOption("thickness", o => o.Thickness = e.NewValue);
        };
        FontSizeSlider.ValueChanged += (_, e) =>
        {
            FontSizeValue.Text = $"{e.NewValue:0} px";
            ChangeOption("fontsize", o => o.FontSize = e.NewValue);
        };
        CounterSizeSlider.ValueChanged += (_, e) =>
        {
            CounterSizeValue.Text = $"{e.NewValue:0} px";
            ChangeOption("countersize", o => o.CounterSize = e.NewValue);
        };
        StrengthSlider.ValueChanged += (_, e) =>
        {
            StrengthValue.Text = $"{e.NewValue:0}%";
            ChangeOption("strength", o => o.Strength = e.NewValue);
        };

        BoldToggle.Click += (_, _) => ChangeOption("bold", o => o.Bold = BoldToggle.IsChecked == true);
        ShadowCheck.Click += (_, _) => ChangeOption("shadow", o => o.Shadow = ShadowCheck.IsChecked == true);

        StylePlain.Checked += (_, _) => ChangeOption("style", o => o.TextStyle = TextStyle.Plain, refreshBar: true);
        StyleLabel.Checked += (_, _) => ChangeOption("style", o => o.TextStyle = TextStyle.Label, refreshBar: true);
        StyleCallout.Checked += (_, _) => ChangeOption("style", o => o.TextStyle = TextStyle.Callout, refreshBar: true);
        ModePixelate.Checked += (_, _) => ChangeOption("mode", o => o.ObfuscateMode = ObfuscateMode.Pixelate);
        ModeBlur.Checked += (_, _) => ChangeOption("mode", o => o.ObfuscateMode = ObfuscateMode.Blur);
        HeadsEnd.Checked += (_, _) => ChangeOption("heads", o => o.ArrowHeads = ArrowHeads.End);
        HeadsBoth.Checked += (_, _) => ChangeOption("heads", o => o.ArrowHeads = ArrowHeads.Both);
        HeadsNone.Checked += (_, _) => ChangeOption("heads", o => o.ArrowHeads = ArrowHeads.None);
    }

    /// <summary>
    /// Applies an option to the tool's defaults (so the next markup uses it) and to the
    /// selected markup, if there is one.
    /// </summary>
    private void ChangeOption(string name, Action<ToolOptions> change, bool refreshBar = false)
    {
        if (_syncingOptions)
            return;
        ToolKind kind = ActiveKind;
        if (kind == ToolKind.Select)
            return;
        change(_settings.GetToolOptions(kind));
        _canvas.ApplyToSelection(name, change);
        if (refreshBar)
            RefreshOptionsBar();
    }

    /// <summary>True if the element is part of the options bar, including its popups.</summary>
    private bool IsInsideOptionsUi(DependencyObject d)
    {
        DependencyObject? current = d;
        while (current != null)
        {
            if (current == OptionsPanel)
                return true;
            DependencyObject? parent = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : null;
            current = parent ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void UpdateCommandStates()
    {
        UndoButton.IsEnabled = _canvas.CanUndo;
        RedoButton.IsEnabled = _canvas.CanRedo;
        DeleteButton.IsEnabled = _canvas.Selected != null;
    }

    // ---- Commands ---------------------------------------------------------------------------

    private void Copy()
    {
        try
        {
            ImageExport.CopyToClipboard(_canvas.RenderImage());
            ShowToast("Copied to clipboard", null);
            if (_settings.CloseEditorAfterCopy)
                Close();
        }
        catch (Exception ex)
        {
            ShowToast("Could not copy: " + ex.Message, null);
        }
    }

    private void QuickSave()
    {
        try
        {
            string path = _settings.NextFilePath();
            ImageExport.Save(_canvas.RenderImage(), path, _settings);
            ShowToast($"Saved {Path.GetFileName(path)}", path);
        }
        catch (Exception ex)
        {
            ShowSaveError(ex, _settings.SaveFolder);
        }
    }

    /// <summary>
    /// Explains a failed save. The common cause is Windows Security's "Controlled folder access",
    /// which blocks unknown apps from writing to Pictures and Documents and reports the folder
    /// as missing or access as denied.
    /// </summary>
    private void ShowSaveError(Exception ex, string folder)
    {
        string message = ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException
            ? $"Windows blocked saving to {folder}. If Controlled folder access is on, allow Shottr in Windows Security, or choose another folder in Settings."
            : "Could not save: " + ex.Message;
        ShowToast(message, null, seconds: 8);
    }

    private void SaveAs()
    {
        string suggested = Path.GetFileName(_settings.NextFilePath());
        var dialog = new SaveFileDialog
        {
            Title = "Save screenshot",
            FileName = suggested,
            InitialDirectory = Directory.Exists(_settings.SaveFolder) ? _settings.SaveFolder : null,
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FilterIndex = _settings.Format == Core.ImageFormat.Jpg ? 2 : 1,
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            ImageExport.Save(_canvas.RenderImage(), dialog.FileName, _settings);
            ShowToast($"Saved {Path.GetFileName(dialog.FileName)}", dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowSaveError(ex, Path.GetDirectoryName(dialog.FileName) ?? dialog.FileName);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Copy();
    private void Save_Click(object sender, RoutedEventArgs e) => QuickSave();
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _canvas.Undo();
        RefreshOptionsBar();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        _canvas.Redo();
        RefreshOptionsBar();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => _canvas.DeleteSelected();

    private void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowSettings();

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSavedPath != null && File.Exists(_lastSavedPath))
            Process.Start("explorer.exe", $"/select,\"{_lastSavedPath}\"");
    }

    // ---- Toast ------------------------------------------------------------------------------

    private void ShowToast(string message, string? savedPath, double? seconds = null)
    {
        _lastSavedPath = savedPath;
        ToastText.Text = message;
        ToastLink.Visibility = savedPath != null ? Visibility.Visible : Visibility.Collapsed;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds ?? (savedPath != null ? 4 : 2.2));
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            if (Toast.Opacity == 0)
                Toast.Visibility = Visibility.Collapsed;
        };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    // ---- Keyboard ---------------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Leave keys alone while the user is typing, either in a text markup or in a text field.
        if (_canvas.IsEditingText || Keyboard.FocusedElement is TextBoxBase)
            return;

        ModifierKeys mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        switch (key)
        {
            case Key.C when ctrl: Copy(); return;
            case Key.S when ctrl && shift: SaveAs(); return;
            case Key.S when ctrl: QuickSave(); return;
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl: Redo_Click(this, e); return;
            case Key.Z when ctrl: Undo_Click(this, e); return;
            case Key.D0 or Key.NumPad0 when ctrl: SetZoom(1, fit: true); return;
            case Key.D1 or Key.NumPad1 when ctrl: SetZoom(1, fit: false); return;
            case Key.OemPlus or Key.Add when ctrl: ZoomBy(1); return;
            case Key.OemMinus or Key.Subtract when ctrl: ZoomBy(-1); return;
            case Key.Delete or Key.Back: _canvas.DeleteSelected(); return;
            case Key.Escape:
                if (_canvas.Selected != null)
                    _canvas.Select(null);
                else
                    Close();
                return;
        }

        if (mods == ModifierKeys.None || mods == ModifierKeys.Shift)
        {
            double step = shift ? 10 : 1;
            switch (key)
            {
                case Key.Left: _canvas.Nudge(new Vector(-step, 0)); return;
                case Key.Right: _canvas.Nudge(new Vector(step, 0)); return;
                case Key.Up: _canvas.Nudge(new Vector(0, -step)); return;
                case Key.Down: _canvas.Nudge(new Vector(0, step)); return;
            }
        }

        if (mods == ModifierKeys.None)
        {
            ToolKind? tool = key switch
            {
                Key.V => ToolKind.Select,
                Key.R => ToolKind.Rectangle,
                Key.E => ToolKind.Ellipse,
                Key.A => ToolKind.Arrow,
                Key.T => ToolKind.Text,
                Key.N => ToolKind.Counter,
                Key.O => ToolKind.Obfuscate,
                _ => null,
            };
            if (tool is ToolKind t)
            {
                SetTool(t);
                return;
            }
        }

        e.Handled = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Interop.Native.SetDarkTitleBar(hwnd, ThemeHelper.IsDarkMode);
    }
}
