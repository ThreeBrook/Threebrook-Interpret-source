using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThreeBrookTrans.Services;
using ThreeBrookTrans.Views;

namespace ThreeBrookTrans;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = AppSettings.CreateDefault();
    public static bool IsExiting { get; private set; }
    public static App Instance => (App)Current;

    private const string ShowSettingsEventName = @"Local\ThreeBrookTrans.ShowSettings";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSettingsEvent;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private TranslateWindow? _translateWindow;
    private ClipboardHistory? _history;
    private HistoryWindow? _historyWindow;
    private SettingsWindow? _settingsWindow;
    private ScreenshotOverlay? _screenshotOverlay;
    private bool _readingSelection;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 开发用：把各窗口按浅色 / 深色渲染成 PNG 后退出，不占用单实例、不读写用户数据
        if (e.Args.Length == 2 && e.Args[0] == "--render-previews")
        {
            base.OnStartup(e);
            PreviewRenderer.Run(e.Args[1]);
            IsExiting = true;
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\ThreeBrookTrans.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // 已在运行：通知正在运行的实例打开设置窗口
            if (EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var showSettings))
            {
                showSettings.Set();
                showSettings.Dispose();
            }
            Shutdown();
            return;
        }
        _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        ThreadPool.RegisterWaitForSingleObject(_showSettingsEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        LoadSettings();
        ThemeManager.Initialize();
        // 规范 §5：提示气泡悬停 0.4 秒后出现
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(400));
        _translateWindow = new TranslateWindow();
        _history = new ClipboardHistory();

        _tray = new TrayIcon();
        _tray.InputTranslateRequested += () => _translateWindow.ShowAndFocus();
        // 等托盘菜单消失后再截图，免得把菜单截进去
        _tray.ScreenshotRequested += async () =>
        {
            await Task.Delay(250);
            StartScreenshot(translateMode: false);
        };
        _tray.ScreenshotTranslateRequested += async () =>
        {
            await Task.Delay(250);
            StartScreenshot(translateMode: true);
        };
        _tray.HistoryRequested += ToggleHistory;
        _tray.SettingsRequested += ShowSettings;
        _tray.OpenSettingsFileRequested += () => Process.Start("notepad.exe", $"\"{SettingsStore.SettingsPath}\"");
        _tray.ReloadSettingsRequested += ReloadSettings;
        _tray.OpenDataFolderRequested += () => Process.Start("explorer.exe", $"\"{SettingsStore.DataDirectory}\"");
        _tray.ExitRequested += ExitApp;

        _hotkeys = new HotkeyManager();
        RegisterHotkeys();

        // 启动完成后回收初始化时的临时对象
        _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(CollectGarbage));
    }

    // —— 配置 ——

    /// <summary>界面预览用默认配置，不读用户的 settings.json。</summary>
    internal static void UsePreviewSettings(string theme) => Settings = new AppSettings { Theme = theme, Engines = AppSettings.CreateDefault().Engines };

    private void LoadSettings()
    {
        try
        {
            Settings = SettingsStore.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // 配置文件写错时先备份原文件，再用默认配置运行；之后任何保存都不会让原内容（如 API Key）丢失
            Settings = AppSettings.CreateDefault();
            string backupNote;
            try
            {
                backupNote = SettingsStore.BackupBrokenFile() is { } backup ? $"原文件已备份为：\n{backup}" : "";
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            {
                backupNote = "原文件备份失败：" + backupError.Message;
            }
            MessageBox.Show($"配置文件读取失败，已使用默认配置：\n{ex.Message}\n\n{backupNote}",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReloadSettings()
    {
        LoadSettings();
        OnSettingsChanged();
        _tray?.ShowMessage(AppInfo.Name, "配置已重新加载");
    }

    /// <summary>设置窗口点"保存"后调用。</summary>
    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        try
        {
            SettingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("保存配置失败（本次运行仍按新设置生效）：" + ex.Message, AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        OnSettingsChanged();
    }

    /// <summary>设置窗口录入快捷键时暂停全局快捷键，否则按下已占用的组合会被截走（无法对调两个快捷键）。</summary>
    public void SuspendHotkeys() => _hotkeys?.UnregisterAll();

    public void ResumeHotkeys() => RegisterHotkeys();

    private void OnSettingsChanged()
    {
        ThemeManager.Apply();
        _translateWindow?.LoadTargets();
        _history?.Trim();
        RegisterHotkeys();
    }

    private void ShowSettings()
    {
        if (_settingsWindow != null)
        {
            BringToFront(_settingsWindow);
            return;
        }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        BringToFront(_settingsWindow);
    }

    /// <summary>从托盘或另一个进程触发时，Activate 可能被系统的前台锁拦住，临时置顶确保窗口出现在最前。</summary>
    private static void BringToFront(Window window)
    {
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys == null || _tray == null || _translateWindow == null)
            return;

        _hotkeys.UnregisterAll();
        var hotkeys = Settings.Hotkeys;
        _tray.SetHotkeys(hotkeys);

        var failed = new List<string>();
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Register(string name, string gesture, Action handler)
        {
            if (string.IsNullOrWhiteSpace(gesture))
                return;
            var key = HotkeyManager.Normalize(gesture);
            if (used.TryGetValue(key, out var other))
                failed.Add($"{name} {gesture}（与{other}重复）");
            else if (!_hotkeys.Register(gesture, handler))
                failed.Add($"{name} {gesture}（可能被其他程序占用）");
            used[key] = name;
        }
        Register("输入翻译", hotkeys.InputTranslate, _translateWindow.Toggle);
        Register("划词翻译", hotkeys.SelectionTranslate, TranslateSelection);
        Register("截图", hotkeys.Screenshot, () => StartScreenshot(translateMode: false));
        Register("截图翻译", hotkeys.ScreenshotTranslate, () => StartScreenshot(translateMode: true));
        Register("剪贴板历史", hotkeys.History, ToggleHistory);

        if (failed.Count > 0)
            _tray.ShowMessage("快捷键注册失败", string.Join("；", failed) + "。请在设置 → 快捷键中修改。", warning: true);
    }

    // —— 功能入口 ——

    private async void TranslateSelection()
    {
        if (_readingSelection || _translateWindow == null)
            return;

        _readingSelection = true;
        try
        {
            var text = await SelectionReader.GetSelectedTextAsync();
            _translateWindow.ShowAtCursor(PrepareText(text));
        }
        finally
        {
            _readingSelection = false;
        }
    }

    public void TranslateText(string text) => _translateWindow?.ShowAtCursor(text);

    private void StartScreenshot(bool translateMode)
    {
        if (_screenshotOverlay != null)
            return;

        var (image, bounds) = ScreenCapture.CaptureVirtualScreen();
        _screenshotOverlay = new ScreenshotOverlay(image, bounds, translateMode);
        _screenshotOverlay.Closed += (_, _) =>
        {
            _screenshotOverlay = null;
            // 整屏截图位图较大，关闭后及时回收
            Dispatcher.BeginInvoke(CollectGarbage, DispatcherPriority.ApplicationIdle);
        };
        _screenshotOverlay.Show();
    }

    /// <summary>识别图片文字；translate 为 true 时直接翻译，否则只显示识别结果。</summary>
    public async void RecognizeImage(BitmapSource image, bool translate)
    {
        try
        {
            var text = await WindowsOcr.RecognizeAsync(image, Settings.Screenshot.OcrLanguage);
            _translateWindow?.ShowAtCursor(PrepareText(text), translate, emptyHint: "没有识别到文字");
        }
        catch (Exception ex)
        {
            // OCR 走 WinRT，HRESULT 会被映射成各种异常类型；都用托盘提示，不弹程序出错对话框
            _tray?.ShowMessage("文字识别失败", ex.Message, warning: true);
        }
    }

    private static string? PrepareText(string? text) =>
        text != null && Settings.MergeLineBreaks ? TextUtil.MergeLines(text) : text;

    private void ToggleHistory()
    {
        if (_history == null)
            return;
        _historyWindow ??= new HistoryWindow(_history);
        _historyWindow.Toggle();
    }

    public void ClearHistory() => _history?.Clear();

    // —— 退出与异常 ——

    private void ExitApp()
    {
        IsExiting = true;
        _hotkeys?.Dispose();
        _history?.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    /// <summary>
    /// 只做垃圾回收，不再强制裁剪工作集：那样会把 WPF 代码和资源换出内存，下次按快捷键时首帧明显变慢。
    /// </summary>
    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.AppendAllText(Path.Combine(SettingsStore.DataDirectory, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch (IOException)
        {
        }
        MessageBox.Show(e.Exception.Message, AppInfo.Name + " 出错", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
