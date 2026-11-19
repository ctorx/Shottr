using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Microsoft.Win32;
using Shottr.Editor;

namespace Shottr.Core;

public enum ImageFormat
{
    Png,
    Jpg,
}

public enum FullScreenMode
{
    MonitorUnderCursor,
    AllMonitors,
}

/// <summary>User preferences, stored as JSON in %AppData%\Shottr\settings.json.</summary>
public sealed class AppSettings
{
    public Hotkey RegionHotkey { get; set; } = new(ModifierKeys.None, Key.Snapshot);
    public Hotkey WindowHotkey { get; set; } = new(ModifierKeys.Alt, Key.Snapshot);
    public Hotkey FullScreenHotkey { get; set; } = new(ModifierKeys.Control, Key.Snapshot);

    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Shottr");

    /// <summary>File name template. Text inside braces is a .NET date format, e.g. {yyyy-MM-dd}.</summary>
    public string FileNamePattern { get; set; } = "Shottr {yyyy-MM-dd} at {HH.mm.ss}";

    public ImageFormat Format { get; set; } = ImageFormat.Png;
    public int JpegQuality { get; set; } = 90;
    public FullScreenMode FullScreen { get; set; } = FullScreenMode.MonitorUnderCursor;
    public bool ShowMagnifier { get; set; } = true;
    public bool CloseEditorAfterCopy { get; set; }

    /// <summary>The last options used for each drawing tool, so they persist between sessions.</summary>
    public Dictionary<ToolKind, ToolOptions> ToolOptions { get; set; } = new();

    // ---- Persistence ------------------------------------------------------------------------

    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shottr");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A corrupt settings file should not stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception)
        {
            // Saving preferences is best effort.
        }
    }

    public ToolOptions GetToolOptions(ToolKind kind)
    {
        if (!ToolOptions.TryGetValue(kind, out ToolOptions? o))
        {
            o = Editor.ToolOptions.DefaultFor(kind);
            ToolOptions[kind] = o;
        }
        return o;
    }

    // ---- File naming ------------------------------------------------------------------------

    public string Extension => Format == ImageFormat.Jpg ? ".jpg" : ".png";

    /// <summary>Builds a unique path in the save folder from the file name pattern.</summary>
    public string NextFilePath()
    {
        DateTime now = DateTime.Now;
        string name = Regex.Replace(FileNamePattern, @"\{([^}]+)\}", m =>
        {
            try { return now.ToString(m.Groups[1].Value); }
            catch (FormatException) { return m.Value; }
        });
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        if (string.IsNullOrWhiteSpace(name))
            name = "Shottr";

        Directory.CreateDirectory(SaveFolder);
        string path = Path.Combine(SaveFolder, name + Extension);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(SaveFolder, $"{name} ({i}){Extension}");
        return path;
    }

    // ---- Start with Windows -----------------------------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [JsonIgnore]
    public static bool StartWithWindows
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Shottr") is string;
        }
        set
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null)
                return;
            if (value)
                key.SetValue("Shottr", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("Shottr", throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// True when Windows is set to open Snipping Tool on Print Screen, which steals the key.
    /// </summary>
    public static bool WindowsUsesPrintScreen
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
            object? v = key?.GetValue("PrintScreenKeyForSnippingEnabled");
            // Windows 11 defaults this to on when the value is missing.
            return v is not int i || i != 0;
        }
    }
}
