using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ThreebrookInterpret.Services;

/// <summary>按物理像素定位窗口，避免多显示器不同缩放比例时位置错乱。</summary>
public static class WindowPlacement
{
    private const int CursorOffset = 16;

    /// <summary>放在鼠标所在屏幕的水平居中、靠上 1/5 处。</summary>
    public static void CenterOnCursorScreen(Window window)
    {
        var hwnd = Handle(window);
        GetCursorPos(out var cursor);
        var work = GetWorkArea(cursor);
        GetWindowRect(hwnd, out var rect);
        MoveTo(hwnd, work.Left + (work.Right - work.Left - (rect.Right - rect.Left)) / 2, work.Top + (work.Bottom - work.Top) / 5);
    }

    /// <summary>放在鼠标右下方，并保证不超出屏幕。</summary>
    public static void MoveNearCursor(Window window)
    {
        GetCursorPos(out var cursor);
        MoveTo(Handle(window), cursor.X + CursorOffset, cursor.Y + CursorOffset);
        ClampToWorkArea(window);
    }

    public static void GetCursorPosition(out int x, out int y)
    {
        GetCursorPos(out var cursor);
        x = cursor.X;
        y = cursor.Y;
    }

    public static void ClampToWorkArea(Window window)
    {
        var hwnd = Handle(window);
        GetWindowRect(hwnd, out var rect);
        var work = GetWorkArea(new POINT { X = rect.Left, Y = rect.Top });
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var x = Math.Max(work.Left, Math.Min(rect.Left, work.Right - width));
        var y = Math.Max(work.Top, Math.Min(rect.Top, work.Bottom - height));
        if (x != rect.Left || y != rect.Top)
            MoveTo(hwnd, x, y);
    }

    private static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

    private static RECT GetWorkArea(POINT point)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(point, 2 /* MONITOR_DEFAULTTONEAREST */), ref info);
        return info.rcWork;
    }

    private static void MoveTo(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 /* NOSIZE | NOZORDER | NOACTIVATE */);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }
}
