using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ThreeBrookTrans.Services;

/// <summary>
/// 截图时的窗口识别：在遮罩出现前，按层叠顺序（最上层在前）记下所有可见顶层窗口的可见区域（物理像素），
/// 鼠标移到哪个窗口就高亮它，单击直接选中。桌面上没有窗口的地方返回所在显示器的整块区域。
/// </summary>
public static class WindowFinder
{
    public static List<System.Drawing.Rectangle> Snapshot()
    {
        var ownPid = (uint)Environment.ProcessId;
        var result = new List<System.Drawing.Rectangle>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd))
                return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == ownPid)
                return true;
            var className = new StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return true; // 桌面、任务栏交给"显示器整块"处理

            var rect = VisibleBounds(hwnd);
            if (rect.Width >= 8 && rect.Height >= 8)
                result.Add(rect);
            return true;
        }, IntPtr.Zero);

        // 最后是各显示器整块（含任务栏），鼠标在桌面上时选中整个屏幕
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            result.Add(screen.Bounds);
        return result;
    }

    /// <summary>DWM 的可见边框（不含 Windows 10/11 窗口外圈看不见的缩放边），取不到时用窗口矩形。</summary>
    private static System.Drawing.Rectangle VisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out frame);
        return System.Drawing.Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
    }

    /// <summary>被 DWM 隐藏的窗口（其他虚拟桌面、挂起的 UWP 应用）虽然"可见"但看不到，要排除。</summary>
    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
