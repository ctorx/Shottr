using System;
using System.Collections.Generic;
using System.Windows.Interop;
using Shottr.Interop;

namespace Shottr.Core;

/// <summary>Registers system-wide hotkeys on a hidden message-only window.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("ShottrHotkeys") { ParentWindow = Native.HWND_MESSAGE, Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a hotkey. Returns false if another app already owns the combination.</summary>
    public bool Register(Hotkey hotkey, Action action)
    {
        if (hotkey.IsEmpty)
            return true;
        int id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, hotkey.NativeModifiers, hotkey.VirtualKey))
            return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _actions.Keys)
            Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out Action? action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
