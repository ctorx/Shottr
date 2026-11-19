using System.Windows.Controls;
using System.Windows.Input;
using Shottr.Core;

namespace Shottr.Controls;

/// <summary>A read-only text box that records the next key combination the user presses.</summary>
public sealed class HotkeyBox : TextBox
{
    private Hotkey _hotkey = Hotkey.None;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        // Implicit styles do not apply to subclasses, so borrow the themed TextBox style explicitly.
        SetResourceReference(StyleProperty, typeof(TextBox));
    }

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Text = value.ToString();
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Text = "Press a key combination…";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Text = _hotkey.ToString();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
            return;

        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = false;
            return;
        }

        if (key is Key.Back or Key.Delete or Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Hotkey = Hotkey.None;
            return;
        }
        Hotkey = new Hotkey(Keyboard.Modifiers, key);
    }

    // Print Screen only sends a key-up message, so it has to be caught here.
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Snapshot)
        {
            Hotkey = new Hotkey(Keyboard.Modifiers, key);
            e.Handled = true;
        }
    }
}
