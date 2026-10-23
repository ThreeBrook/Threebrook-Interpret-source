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

        // 启动完成后释放初始化时的临时内存，托盘常驻时占用更小
        _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(TrimMemory));
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
        catch (Exception ex)
        {
            // 配置文件写错时不覆盖它，使用默认配置运行
            Settings = AppSettings.CreateDefault();
            MessageBox.Show($"配置文件读取失败，已使用默认配置：\n{ex.Message}\n\n{SettingsStore.SettingsPath}",
                "ThreeBrook Trans", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReloadSettings()
    {
        LoadSettings();
        OnSettingsChanged();
        _tray?.ShowMessage("ThreeBrook Trans", "配置已重新加载");
    }

    /// <summary>设置窗口点"保存"后调用。</summary>
    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        try
        {
            SettingsStore.Save(settings);
        }
        catch (IOException ex)
        {
            MessageBox.Show("保存配置失败：" + ex.Message, "ThreeBrook Trans", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        OnSettingsChanged();
    }

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
        void Register(string name, string gesture, Action handler)
        {
            if (!string.IsNullOrWhiteSpace(gesture) && !_hotkeys.Register(gesture, handler))
                failed.Add($"{name} {gesture}");
        }
        Register("输入翻译", hotkeys.InputTranslate, _translateWindow.Toggle);
        Register("划词翻译", hotkeys.SelectionTranslate, TranslateSelection);
        Register("截图", hotkeys.Screenshot, () => StartScreenshot(translateMode: false));
        Register("截图翻译", hotkeys.ScreenshotTranslate, () => StartScreenshot(translateMode: true));
        Register("剪贴板历史", hotkeys.History, ToggleHistory);

        if (failed.Count > 0)
            _tray.ShowMessage("快捷键注册失败",
                string.Join("、", failed) + " 可能已被其他程序占用，请在设置 → 快捷键中修改。", warning: true);
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
            Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);
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
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
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

    private static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

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
        MessageBox.Show(e.Exception.Message, "ThreeBrook Trans 出错", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
