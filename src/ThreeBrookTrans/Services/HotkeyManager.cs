using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace ThreeBrookTrans.Services;

/// <summary>基于 RegisterHotKey 的全局快捷键，使用隐藏的消息窗口接收 WM_HOTKEY。</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        // HWND_MESSAGE (-3)：只收消息、不可见的窗口
        _source = new HwndSource(new HwndSourceParameters("ThreeBrookTransHotkeys") { ParentWindow = new IntPtr(-3) });
        _source.AddHook(WndProc);
    }

    /// <summary>注册快捷键，例如 "Alt+Q"、"Ctrl+Shift+F1"。失败时返回 false（格式错误或已被其他程序占用）。</summary>
    public bool Register(string gesture, Action handler)
    {
        if (!TryParse(gesture, out var modifiers, out var vk))
            return false;

        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, vk))
            return false;

        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys)
            UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handler();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>统一写法后的比较键，"alt+q" 与 "Alt+Q" 视为同一个快捷键。</summary>
    public static string Normalize(string gesture) =>
        TryParse(gesture, out var modifiers, out var vk) ? $"{modifiers}:{vk}" : gesture.Trim().ToUpperInvariant();

    private static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture))
            return false;

        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win" or "windows": modifiers |= MOD_WIN; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key))
                        return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
