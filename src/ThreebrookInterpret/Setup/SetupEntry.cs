using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Setup;

/// <summary>
/// 安装模式入口。同一个程序按情况进入不同模式：
///   安装包（末尾有尾标）            → 安装向导；--silent [--dir D] [--desktop] [--autostart] 静默安装
///   ThreebrookInterpret.exe --uninstall → 卸载向导；--silent [--delete-data] 静默卸载
///   --test                          → 测试模式（另一个 AppId 和名字，不碰开机自启和真实数据）
/// 静默模式的结果写到 %TEMP%\ThreebrookInterpret-Setup.log，退出码 0 成功、1 失败。
/// </summary>
internal static class SetupEntry
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ThreebrookInterpret-Setup.log");

    /// <summary>不是安装模式时返回 false，程序照常启动。</summary>
    public static bool TryRun(Application app, string[] args)
    {
        var uninstall = args.Contains("--uninstall");
        SetupPayload? payload = null;
        if (!uninstall)
        {
            try
            {
                payload = SetupPayload.TryOpen(Environment.ProcessPath);
            }
            catch (InvalidDataException ex)
            {
                MessageBox.Show(ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
                app.Shutdown(1);
                return true;
            }
            if (payload == null)
                return false;
        }

        var context = new SetupContext(args.Contains("--test"));
        var silent = args.Contains("--silent");
        var directory = Value(args, "--dir");

        if (uninstall)
        {
            directory ??= Path.GetDirectoryName(Environment.ProcessPath)!;
            // 从安装目录运行时先把自己拷到临时文件夹再启动，安装目录才能整个删掉
            if (!args.Contains("--from-temp"))
            {
                RelaunchFromTemp(args, directory);
                app.Shutdown();
                return true;
            }
            app.Exit += (_, _) => DeleteSelfLater();
            if (silent)
            {
                app.Shutdown(RunSilent(() => SetupEngine.Uninstall(directory, args.Contains("--delete-data"), context, new Progress<SetupProgress>()),
                    $"uninstall {directory}"));
                return true;
            }
            ShowWindow(app, new SetupWindow(directory, context));
            return true;
        }

        if (silent)
        {
            var existing = SetupEngine.FindExisting(context);
            var options = new InstallOptions(
                directory != null ? Path.GetFullPath(directory) : existing?.Directory ?? context.DefaultDirectory,
                args.Contains("--desktop") || existing?.Desktop == true,
                args.Contains("--autostart") || existing?.AutoStart == true);
            app.Shutdown(RunSilent(() => SetupEngine.Install(payload!, options, context, new Progress<SetupProgress>(), CancellationToken.None),
                $"install {options.Directory}"));
            return true;
        }
        ShowWindow(app, new SetupWindow(payload!, context));
        return true;
    }

    private static void ShowWindow(Application app, Window window)
    {
        Loc.Apply(SavedLanguage());
        ThemeManager.Initialize();
        window.Closed += (_, _) => app.Shutdown();
        window.Show();
        window.Activate();
    }

    /// <summary>覆盖安装、卸载时沿用软件里选的界面语言；只读配置，不写。</summary>
    private static string SavedLanguage()
    {
        try
        {
            if (File.Exists(SettingsStore.SettingsPath))
                return SettingsStore.Load().Language;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        return "auto";
    }

    private static int RunSilent(Action work, string what)
    {
        try
        {
            work();
            Log($"{what}: ok");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"{what}: failed: {ex.Message}");
            return 1;
        }
    }

    private static void RelaunchFromTemp(string[] args, string directory)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"ThreebrookInterpret-Uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(Environment.ProcessPath!, copy);
        var start = new ProcessStartInfo(copy) { UseShellExecute = false };
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dir")
                i++; // 连同值一起跳过，下面统一加
            else
                start.ArgumentList.Add(args[i]);
        }
        start.ArgumentList.Add("--dir");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add("--from-temp");
        Process.Start(start);
    }

    /// <summary>临时文件夹里的卸载程序退出后，由隐藏的 cmd 把自己删掉。</summary>
    private static void DeleteSelfLater()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{Environment.ProcessPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {AppInfo.Version} {message}\n");
        }
        catch (IOException)
        {
        }
    }
}
