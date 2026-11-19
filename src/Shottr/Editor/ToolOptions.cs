using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Shottr.Editor;

public enum ToolKind
{
    Select,
    Rectangle,
    Ellipse,
    Arrow,
    Text,
    Counter,
    Obfuscate,
}

public enum TextStyle
{
    Plain,
    Label,
    Callout,
}

public enum ObfuscateMode
{
    Pixelate,
    Blur,
}

public enum ArrowHeads
{
    End,
    Both,
    None,
}

/// <summary>
/// Every option any tool can have. Each tool reads only the ones it uses. Colors are stored
/// as "#AARRGGBB" strings so the settings file stays readable.
/// </summary>
public sealed class ToolOptions
{
    public string Stroke { get; set; } = Palette.Red;
    public string Fill { get; set; } = Palette.Transparent;
    public double Thickness { get; set; } = 4;
    public double FontSize { get; set; } = 22;
    public bool Bold { get; set; } = true;
    public TextStyle TextStyle { get; set; } = TextStyle.Label;
    public double CounterSize { get; set; } = 36;
    public ObfuscateMode ObfuscateMode { get; set; } = ObfuscateMode.Pixelate;
    public double Strength { get; set; } = 50;
    public bool Shadow { get; set; } = true;
    public ArrowHeads ArrowHeads { get; set; } = ArrowHeads.End;

    [JsonIgnore]
    public Color StrokeColor
    {
        get => Palette.Parse(Stroke);
        set => Stroke = Palette.ToHex(value);
    }

    [JsonIgnore]
    public Color FillColor
    {
        get => Palette.Parse(Fill);
        set => Fill = Palette.ToHex(value);
    }

    public ToolOptions Clone() => (ToolOptions)MemberwiseClone();

    public static ToolOptions DefaultFor(ToolKind kind) => kind switch
    {
        ToolKind.Arrow => new ToolOptions { Thickness = 5 },
        ToolKind.Text => new ToolOptions { Stroke = Palette.White, Fill = Palette.Red, TextStyle = TextStyle.Label },
        ToolKind.Counter => new ToolOptions { Fill = Palette.Red, CounterSize = 36 },
        ToolKind.Obfuscate => new ToolOptions { Shadow = false },
        _ => new ToolOptions(),
    };
}

/// <summary>The preset colors offered in the color picker.</summary>
public static class Palette
{
    public const string Transparent = "#00000000";
    public const string Red = "#FFE5484D";
    public const string White = "#FFFFFFFF";

    public static readonly string[] Colors =
    {
        "#FFE5484D", "#FFF76B15", "#FFFFC53D", "#FF30A46C", "#FF12A594", "#FF0090FF",
        "#FF3E63DD", "#FF8E4EC6", "#FFD6409F", "#FFFFFFFF", "#FF8B8D98", "#FF1C2024",
    };

    public static Color Parse(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return System.Windows.Media.Colors.Red;
        }
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Picks black or white, whichever reads better on the given background.</summary>
    public static Color ContrastText(Color bg)
    {
        double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255;
        return lum > 0.62 ? Color.FromRgb(0x1C, 0x20, 0x24) : System.Windows.Media.Colors.White;
    }
}
