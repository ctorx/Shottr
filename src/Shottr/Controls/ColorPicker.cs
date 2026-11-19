using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Shottr.Editor;

namespace Shottr.Controls;

/// <summary>A swatch button that opens a popup of preset colors plus a hex entry box.</summary>
public sealed class ColorPicker : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(ColorPicker),
        new PropertyMetadata(Colors.Red, (d, _) => ((ColorPicker)d).UpdateSwatch()));

    private readonly ToggleButton _button;
    private readonly Border _swatch;
    private readonly Popup _popup;
    private readonly TextBox _hex;
    private readonly UniformGrid _grid;
    private readonly Border _noFill;

    public ColorPicker()
    {
        _swatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
        };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

        var chevron = new Icon { Size = 14, Data = Geometry.Parse("M7,10 L12,15 L17,10"), Margin = new Thickness(6, 0, 0, 0) };
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_swatch);
        content.Children.Add(chevron);

        _button = new ToggleButton
        {
            Content = content,
            Height = 36,
            Padding = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _button.SetResourceReference(StyleProperty, "OptionToggleButton");

        _grid = new UniformGrid { Columns = 6 };
        foreach (string hex in Palette.Colors)
            _grid.Children.Add(MakeSwatch(Palette.Parse(hex), 28));

        _noFill = MakeSwatch(Colors.Transparent, 28);
        var noFillRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0) };
        noFillRow.Children.Add(_noFill);
        var noFillLabel = new TextBlock { Text = "No fill", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        noFillRow.Children.Add(noFillLabel);
        noFillRow.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(AllowTransparent))
        {
            Source = this,
            Converter = new BooleanToVisibilityConverter(),
        });

        _hex = new TextBox { Width = 120, VerticalContentAlignment = VerticalAlignment.Center };
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyHex();
                e.Handled = true;
            }
        };
        _hex.LostKeyboardFocus += (_, _) => ApplyHex();
        var hexRow = new DockPanel { Margin = new Thickness(2, 10, 2, 0) };
        var hexLabel = new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        hexLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        hexRow.Children.Add(hexLabel);
        hexRow.Children.Add(_hex);

        var panel = new StackPanel();
        panel.Children.Add(_grid);
        panel.Children.Add(noFillRow);
        panel.Children.Add(hexRow);

        var card = new Border
        {
            Child = panel,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 4, 8, 12),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.25, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        card.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");

        _popup = new Popup
        {
            Child = card,
            PlacementTarget = _button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        _button.Checked += (_, _) =>
        {
            _hex.Text = Palette.ToHex(SelectedColor);
            _popup.IsOpen = true;
        };
        _button.Unchecked += (_, _) => _popup.IsOpen = false;
        // While the popup is open, ignore clicks on the button so a click closes rather than reopens it.
        _popup.Opened += (_, _) => _button.IsHitTestVisible = false;
        _popup.Closed += (_, _) =>
        {
            _button.IsChecked = false;
            _button.IsHitTestVisible = true;
        };

        Content = new Grid { Children = { _button, _popup } };
        UpdateSwatch();
    }

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>When true the popup offers a "No fill" choice.</summary>
    public bool AllowTransparent { get; set; }

    /// <summary>Raised only when the user picks a color, not when code sets one.</summary>
    public event EventHandler? ColorPicked;

    /// <summary>The popup, so the editor can tell when focus is inside it.</summary>
    public Popup PopupElement => _popup;

    private Border MakeSwatch(Color color, double size)
    {
        var b = new Border
        {
            Width = size,
            Height = size,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(1),
            Background = SwatchBrush(color),
            Cursor = Cursors.Hand,
            ToolTip = color.A == 0 ? "No fill" : Palette.ToHex(color),
        };
        b.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        b.MouseEnter += (_, _) => b.BorderThickness = new Thickness(2.5);
        b.MouseLeave += (_, _) => b.BorderThickness = new Thickness(1);
        b.MouseLeftButtonUp += (_, _) => Pick(color);
        return b;
    }

    private void Pick(Color color)
    {
        SelectedColor = color;
        _popup.IsOpen = false;
        ColorPicked?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyHex()
    {
        if (!_popup.IsOpen)
            return;
        string text = _hex.Text.Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(text);
            if (c != SelectedColor)
            {
                SelectedColor = c;
                ColorPicked?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (FormatException)
        {
            _hex.Text = Palette.ToHex(SelectedColor);
        }
    }

    private void UpdateSwatch()
    {
        if (_swatch != null)
            _swatch.Background = SwatchBrush(SelectedColor);
    }

    /// <summary>A solid brush, or white with a red slash for "no color".</summary>
    private static Brush SwatchBrush(Color c)
    {
        if (c.A > 0)
            return new SolidColorBrush(c);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 10, 10))));
        group.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), 1.2),
            new LineGeometry(new Point(1.5, 8.5), new Point(8.5, 1.5))));
        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }
}
