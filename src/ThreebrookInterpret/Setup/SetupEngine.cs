using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Setup;

/// <summary>
/// 安装模式的公共信息。测试模式（--test）换一个 AppId 和名字，不碰开机自启项和真实数据，
/// 用来在开发机上反复验证安装 / 卸载而不影响正在用的那份。
/// </summary>
internal sealed class SetupContext(bool test)
{
    public bool Test { get; } = test;
    public string AppId => Test ? "ThreebrookInterpret.SetupTest" : "ThreebrookInterpret";
    /// <summary>卸载信息里显示的名字：中文界面"三溪通译 Threebrook Interpret"，英文界面"Threebrook Interpret"。</summary>
    public string DisplayName => AppInfo.FullName + (Test ? Loc.T("Setup.TestSuffix") : "");
    public string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Test ? "ThreebrookInterpret.SetupTest" : "ThreebrookInterpret");
    public string StartMenuLink => LinkPath(Environment.SpecialFolder.Programs, AppInfo.Name);
    public string DesktopLink => LinkPath(Environment.SpecialFolder.DesktopDirectory, AppInfo.Name);

    /// <summary>快捷方式按安装时的界面语言命名；找、删的时候中英文两个名字都看，换了语言再卸载也删得掉。</summary>
    public string[] AllLinks => [.. new[] { AppInfo.ChineseName, AppInfo.EnglishName }
        .SelectMany(name => new[] { LinkPath(Environment.SpecialFolder.Programs, name), LinkPath(Environment.SpecialFolder.DesktopDirectory, name) })];

    public string[] AllDesktopLinks => [LinkPath(Environment.SpecialFolder.DesktopDirectory, AppInfo.ChineseName), LinkPath(Environment.SpecialFolder.DesktopDirectory, AppInfo.EnglishName)];

    private string LinkPath(Environment.SpecialFolder folder, string name) =>
        Path.Combine(Environment.GetFolderPath(folder), name + (Test ? (name == AppInfo.ChineseName ? "（安装测试）" : " (setup test)") : "") + ".lnk");
    public string UninstallKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;
}

internal sealed record InstallOptions(string Directory, bool Desktop, bool AutoStart);

/// <summary>已经装过的那份（从卸载信息里读）。</summary>
internal sealed record ExistingInstall(string Directory, string Version, bool Desktop, bool AutoStart);

/// <param name="Fraction">0–1。</param>
internal sealed record SetupProgress(double Fraction, string File);

/// <summary>
/// 安装 / 卸载引擎（参考三溪寻影自制安装程序 D-168）：只给当前用户装，不需要管理员权限，不联网。
/// 安装时新文件先写成 .tbs-new，全部写完再把旧文件改名 .tbs-old、新文件换上；出错或中止就按记录撤回，
/// 原来的版本照常可用。全部换好才写快捷方式、卸载信息和 install.json。
/// </summary>
internal static class SetupEngine
{
    public const string ExeName = "ThreebrookInterpret.exe";
    public const string ManifestName = "install.json";
    public const string QuitEventName = @"Local\ThreebrookInterpret.Quit";
    private const string NewSuffix = ".tbs-new";
    private const string OldSuffix = ".tbs-old";

    /// <summary>测试开关：写第 n+1 个文件前 / 全部换好后（end）模拟出错，验证回滚。</summary>
    private static readonly string? FailAt = Environment.GetEnvironmentVariable("THREEBROOK_SETUP_FAIL_AT");

    public static ExistingInstall? FindExisting(SetupContext context)
    {
        using var key = Registry.CurrentUser.OpenSubKey(context.UninstallKey);
        if (key?.GetValue("InstallLocation") is not string directory || !File.Exists(Path.Combine(directory, ExeName)))
            return null;
        return new ExistingInstall(directory, key.GetValue("DisplayVersion") as string ?? "",
            context.AllDesktopLinks.Any(File.Exists), !context.Test && AutoStartPointsInto(directory));
    }

    /// <summary>用户选的文件夹不是以产品名结尾时，在下面建一个产品文件夹（选 D:\Apps 装到 D:\Apps\ThreebrookInterpret）。</summary>
    public static string NormalizeDirectory(string directory, SetupContext context)
    {
        directory = Path.GetFullPath(directory.Trim().Trim('"'));
        var name = Path.GetFileName(context.DefaultDirectory);
        return Path.GetFileName(directory.TrimEnd('\\')).Equals(name, StringComparison.OrdinalIgnoreCase)
            ? directory.TrimEnd('\\')
            : Path.Combine(directory, name);
    }

    public static long FreeSpace(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    // —— 安装 ——

    public static void Install(SetupPayload payload, InstallOptions options, SetupContext context,
        IProgress<SetupProgress> progress, CancellationToken cancel)
    {
        var directory = options.Directory;
        if (!Path.IsPathFullyQualified(directory))
            throw new SetupException(Loc.F("Setup.FullPathNeeded", context.DefaultDirectory));
        var free = FreeSpace(directory);
        var need = payload.TotalSize + payload.TotalSize / 10;
        if (free >= 0 && free < need)
            throw new SetupException(Loc.F("Setup.DiskFull", FormatSize(need), Path.GetPathRoot(directory)?.TrimEnd('\\'), FormatSize(free)));

        CloseRunning(directory);
        var createdDirectories = new List<string>();
        CreateDirectory(directory, createdDirectories);
        CleanLeftovers(directory);

        // 1. 全部写成 .tbs-new
        var written = new List<string>();
        var replaced = new List<string>();
        var swapped = new List<string>();
        try
        {
            long done = 0;
            var total = Math.Max(1, payload.TotalSize);
            for (var i = 0; i < payload.Files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                if (FailAt == i.ToString())
                    throw new IOException($"测试：写第 {i + 1} 个文件前模拟出错");
                var file = payload.Files[i];
                var target = Path.Combine(directory, file.Path);
                CreateDirectory(Path.GetDirectoryName(target)!, createdDirectories);
                written.Add(target);
                using (var source = payload.Open(file))
                using (var output = new FileStream(target + NewSuffix, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        done += read;
                        progress.Report(new SetupProgress((double)done / total, file.Path));
                    }
                }
            }

            // 2. 换上：旧文件改名 .tbs-old，新文件改成正式名字
            cancel.ThrowIfCancellationRequested();
            foreach (var target in written)
            {
                if (File.Exists(target))
                {
                    File.Move(target, target + OldSuffix, overwrite: true);
                    replaced.Add(target);
                }
                File.Move(target + NewSuffix, target);
                swapped.Add(target);
            }
            if (FailAt == "end")
                throw new IOException("测试：全部换好后模拟出错");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            progress.Report(new SetupProgress(-1, "rollback"));
            Rollback(written, replaced, swapped, createdDirectories);
            if (ex is OperationCanceledException)
                throw;
            throw new SetupException(ex is UnauthorizedAccessException
                ? Loc.F("Setup.NoPermission", directory)
                : Loc.F("Setup.WriteFailed", ex.Message));
        }

        // 3. 提交：删旧文件，删上一版有、这一版没有的文件，再登记
        foreach (var target in replaced)
            TryDelete(target + OldSuffix);
        var previous = ReadManifest(directory);
        var current = payload.Files.Select(f => f.Path).ToList();
        foreach (var stale in previous.Except(current, StringComparer.OrdinalIgnoreCase))
            TryDelete(Path.Combine(directory, stale));
        RemoveEmptyDirectories(directory, keepRoot: true);

        var exe = Path.Combine(directory, ExeName);
        File.WriteAllText(Path.Combine(directory, ManifestName), JsonSerializer.Serialize(new Manifest
        {
            Version = AppInfo.Version,
            Files = current,
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var link in context.AllLinks)
            TryDelete(link); // 上次可能用另一种语言命名
        ShellLink.Create(context.StartMenuLink, exe, AppInfo.FullName);
        if (options.Desktop)
            ShellLink.Create(context.DesktopLink, exe, AppInfo.FullName);
        if (!context.Test)
            AutoStart.Set(options.AutoStart, exe);
        WriteUninstallInfo(directory, payload.TotalSize, context);
        progress.Report(new SetupProgress(1, ""));
    }

    private static void Rollback(List<string> written, List<string> replaced, List<string> swapped, List<string> createdDirectories)
    {
        foreach (var target in written)
        {
            TryDelete(target + NewSuffix);
            if (swapped.Contains(target))
                TryDelete(target); // 换上去的新文件
            if (replaced.Contains(target))
            {
                try
                {
                    File.Move(target + OldSuffix, target, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        for (var i = createdDirectories.Count - 1; i >= 0; i--)
        {
            try
            {
                Directory.Delete(createdDirectories[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>上次安装被打断（断电、结束进程）留下的临时文件：.tbs-new 删掉，.tbs-old 在原文件不在时改回去。</summary>
    private static void CleanLeftovers(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*" + NewSuffix, SearchOption.AllDirectories).ToList())
            TryDelete(file);
        foreach (var file in Directory.EnumerateFiles(directory, "*" + OldSuffix, SearchOption.AllDirectories).ToList())
        {
            var original = file[..^OldSuffix.Length];
            if (File.Exists(original))
                TryDelete(file);
            else
                File.Move(file, original);
        }
    }

    private static void WriteUninstallInfo(string directory, long size, SetupContext context)
    {
        var exe = Path.Combine(directory, ExeName);
        var test = context.Test ? " --test" : "";
        using var key = Registry.CurrentUser.CreateSubKey(context.UninstallKey);
        key.SetValue("DisplayName", context.DisplayName);
        key.SetValue("DisplayVersion", AppInfo.Version);
        key.SetValue("Publisher", AppInfo.Brand);
        key.SetValue("URLInfoAbout", AppInfo.ProductUrl);
        key.SetValue("HelpLink", AppInfo.ProductUrl);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", directory);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall{test}");
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --silent{test}");
        key.SetValue("EstimatedSize", (int)Math.Max(1, size / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // —— 卸载 ——

    public static void Uninstall(string directory, bool deleteData, SetupContext context, IProgress<SetupProgress> progress)
    {
        CloseRunning(directory);
        var files = ReadManifest(directory);
        if (files.Count == 0)
            files = [ExeName];
        files.Add(ManifestName);
        for (var i = 0; i < files.Count; i++)
        {
            progress.Report(new SetupProgress((double)i / files.Count, files[i]));
            var path = Path.Combine(directory, files[i]);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new SetupException(Loc.F("Setup.DeleteFailed", path, ex.Message));
                }
            }
        }
        RemoveEmptyDirectories(directory, keepRoot: false);

        foreach (var link in context.AllLinks)
            TryDelete(link);
        Registry.CurrentUser.DeleteSubKeyTree(context.UninstallKey, throwOnMissingSubKey: false);
        if (!context.Test)
        {
            if (AutoStartPointsInto(directory))
                AutoStart.Set(false);
            if (deleteData && Directory.Exists(SettingsStore.InstalledDataDirectory))
                Directory.Delete(SettingsStore.InstalledDataDirectory, recursive: true);
        }
        progress.Report(new SetupProgress(1, ""));
    }

    // —— 关闭正在运行的三溪通译 ——

    /// <summary>
    /// 只处理程序路径在安装目录里的进程：先通知它正常退出（删托盘图标），6 秒后还在就结束进程。
    /// 别处运行的三溪通译（绿色版、开发版）不受影响。
    /// </summary>
    private static void CloseRunning(string directory)
    {
        var exe = Path.Combine(directory, ExeName);
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName))
            .Where(p => p.Id != Environment.ProcessId && string.Equals(ProcessPath(p), exe, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (running.Count == 0)
            return;

        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
        {
            quit.Set();
            quit.Dispose();
        }
        var deadline = DateTime.Now.AddSeconds(6);
        foreach (var process in running)
        {
            var remaining = (int)Math.Max(0, (deadline - DateTime.Now).TotalMilliseconds);
            if (!process.WaitForExit(remaining))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    private static string? ProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // —— 工具 ——

    private static bool AutoStartPointsInto(string directory) =>
        AutoStart.RegisteredPath is { } path && string.Equals(Path.GetDirectoryName(path), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private sealed class Manifest
    {
        public string Version { get; set; } = "";
        public List<string> Files { get; set; } = [];
    }

    private static List<string> ReadManifest(string directory)
    {
        try
        {
            var path = Path.Combine(directory, ManifestName);
            return File.Exists(path) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))?.Files ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void CreateDirectory(string directory, List<string> created)
    {
        var missing = new Stack<string>();
        for (var d = directory; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d))
            missing.Push(d);
        foreach (var d in missing)
        {
            Directory.CreateDirectory(d);
            created.Add(d);
        }
    }

    private static void RemoveEmptyDirectories(string directory, bool keepRoot)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var sub in Directory.GetDirectories(directory))
            RemoveEmptyDirectories(sub, keepRoot: false);
        if (!keepRoot && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            try
            {
                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        _ => $"{Math.Max(1, (long)Math.Ceiling(bytes / (double)(1 << 20)))} MB",
    };
}

/// <summary>给用户看的安装 / 卸载错误（文字直接显示在出错页）。</summary>
internal sealed class SetupException(string message) : Exception(message);
