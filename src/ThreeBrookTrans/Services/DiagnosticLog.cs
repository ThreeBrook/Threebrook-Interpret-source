using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ThreeBrookTrans.Services;

/// <summary>
/// 划词诊断日志（数据目录 selection.log），只记录窗口、方式、耗时和字数，不记录文字内容。
/// 超过 64KB 时清空重写。
/// </summary>
public static class DiagnosticLog
{
    private const long MaxSize = 64 * 1024;

    private static string LogPath => Path.Combine(SettingsStore.DataDirectory, "selection.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxSize)
                info.Delete();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\n");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>前台窗口的进程名和窗口类名，例如 "claude / Chrome_WidgetWin_1"。</summary>
    public static string DescribeForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch (ArgumentException)
        {
            process = "?";
        }
        return $"{process} / {className}";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
