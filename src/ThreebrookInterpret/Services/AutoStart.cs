using System;
using Microsoft.Win32;

namespace ThreebrookInterpret.Services;

/// <summary>开机自启：写入当前用户的 Run 注册表项，不需要管理员权限。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ThreebrookInterpret";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>自启项登记的程序路径；没有自启时为 null。</summary>
    public static string? RegisteredPath
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return (key?.GetValue(ValueName) as string)?.Trim().Trim('"');
        }
    }

    /// <param name="exePath">安装程序给装好的程序登记时传入；不传时用当前程序。</param>
    public static void Set(bool enabled, string? exePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, exePath == null ? Command : $"\"{exePath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
