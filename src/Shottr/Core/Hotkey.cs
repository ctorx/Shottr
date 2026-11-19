using System.Collections.Generic;
using System.Windows.Input;
using Shottr.Interop;

namespace Shottr.Core;

/// <summary>A key plus modifier combination that can be registered as a global hotkey.</summary>
public sealed record Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static readonly Hotkey None = new(ModifierKeys.None, Key.None);

    public bool IsEmpty => Key == Key.None;

    public uint NativeModifiers
    {
        get
        {
            uint m = Native.MOD_NOREPEAT;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) m |= Native.MOD_ALT;
            if (Modifiers.HasFlag(ModifierKeys.Control)) m |= Native.MOD_CONTROL;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) m |= Native.MOD_SHIFT;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) m |= Native.MOD_WIN;
            return m;
        }
    }

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        if (IsEmpty)
            return "None";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        Key.Snapshot => "Print Screen",
        Key.Back => "Backspace",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Prior => "Page Up",
        Key.Next => "Page Down",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString(),
    };
}
