using System;
using System.IO;
using Microsoft.Win32;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 改名迁移（ThreeBrookTrans → ThreebrookInterpret）：新版第一次启动时，
/// 把旧数据目录（配置、剪贴板历史、日志）整体搬到新目录，并把开机自启项换成新程序。
/// 新目录已存在时不动旧目录，避免覆盖。
/// </summary>
public static class LegacyMigration
{
    private const string OldName = "ThreeBrookTrans";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Run()
    {
        MigrateDataDirectory();
        MigrateAutoStart();
    }

    private static void MigrateDataDirectory()
    {
        var oldDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName);
        var newDirectory = SettingsStore.DataDirectory;
        if (!Directory.Exists(oldDirectory) || Directory.Exists(newDirectory))
            return;

        try
        {
            Directory.Move(oldDirectory, newDirectory);
            DiagnosticLog.Write($"migrated data directory {oldDirectory} -> {newDirectory}", "settings.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 搬不动（文件被占用等）就复制一份，旧目录保留
            CopyDirectory(oldDirectory, newDirectory);
            DiagnosticLog.Write($"copied data directory {oldDirectory} -> {newDirectory} ({ex.Message})", "settings.log");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    /// <summary>旧版开着开机自启时，删掉指向旧程序的项，按新程序重新登记。</summary>
    private static void MigrateAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldName) == null)
                return;
            key.DeleteValue(OldName, throwOnMissingValue: false);
            AutoStart.Set(true);
            DiagnosticLog.Write("migrated autostart entry", "settings.log");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            DiagnosticLog.Write("autostart migration failed: " + ex.Message, "settings.log");
        }
    }
}
