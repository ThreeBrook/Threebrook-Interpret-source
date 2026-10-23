using System;
using System.Drawing;
using System.Windows.Forms;

namespace ThreebrookInterpret.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly TrayMenuRenderer _renderer = new();
    private readonly ToolStripMenuItem _inputItem;
    private readonly ToolStripMenuItem _selectionItem;
    private readonly ToolStripMenuItem _screenshotItem;
    private readonly ToolStripMenuItem _screenshotTranslateItem;
    private readonly ToolStripMenuItem _historyItem;

    public event Action? InputTranslateRequested;
    public event Action? ScreenshotRequested;
    public event Action? ScreenshotTranslateRequested;
    public event Action? HistoryRequested;
    public event Action? SettingsRequested;
    public event Action? OpenSettingsFileRequested;
    public event Action? ReloadSettingsRequested;
    public event Action? OpenDataFolderRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _menu = new ContextMenuStrip
        {
            Renderer = _renderer,
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Padding = new Padding(0, 4, 0, 4),
        };
        var menu = _menu;
        _inputItem = new ToolStripMenuItem("输入翻译", null, (_, _) => InputTranslateRequested?.Invoke());
        // 划词需要先在其他程序里选中文字，从菜单触发没有意义，这里只做快捷键提示
        _selectionItem = new ToolStripMenuItem("划词翻译（选中文字后按快捷键）") { Enabled = false };
        _screenshotItem = new ToolStripMenuItem("截图", null, (_, _) => ScreenshotRequested?.Invoke());
        _screenshotTranslateItem = new ToolStripMenuItem("截图翻译", null, (_, _) => ScreenshotTranslateRequested?.Invoke());
        _historyItem = new ToolStripMenuItem("剪贴板历史", null, (_, _) => HistoryRequested?.Invoke());

        var advanced = new ToolStripMenuItem("高级");
        advanced.DropDownItems.Add("打开配置文件", null, (_, _) => OpenSettingsFileRequested?.Invoke());
        advanced.DropDownItems.Add("重新加载配置文件", null, (_, _) => ReloadSettingsRequested?.Invoke());
        advanced.DropDownItems.Add("打开数据目录", null, (_, _) => OpenDataFolderRequested?.Invoke());

        menu.Items.AddRange(new ToolStripItem[]
        {
            _inputItem, _selectionItem, _screenshotItem, _screenshotTranslateItem, _historyItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("设置…", null, (_, _) => SettingsRequested?.Invoke()),
            advanced,
            new ToolStripSeparator(),
            new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()),
        });

        foreach (ToolStripItem item in menu.Items)
            StyleItem(item);
        foreach (ToolStripItem item in advanced.DropDownItems)
            StyleItem(item);
        advanced.DropDown.BackColor = SystemColors.Window;
        ((ToolStripDropDownMenu)advanced.DropDown).ShowImageMargin = false;
        advanced.DropDown.Renderer = _renderer;
        ApplyTheme();
        ThemeManager.Changed += ApplyTheme;

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = AppInfo.Name,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                InputTranslateRequested?.Invoke();
        };
    }

    public void SetHotkeys(HotkeySettings hotkeys)
    {
        _inputItem.ShortcutKeyDisplayString = hotkeys.InputTranslate;
        _selectionItem.ShortcutKeyDisplayString = hotkeys.SelectionTranslate;
        _screenshotItem.ShortcutKeyDisplayString = hotkeys.Screenshot;
        _screenshotTranslateItem.ShortcutKeyDisplayString = hotkeys.ScreenshotTranslate;
        _historyItem.ShortcutKeyDisplayString = hotkeys.History;
    }

    public void ShowMessage(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    private static void StyleItem(ToolStripItem item)
    {
        if (item is ToolStripMenuItem)
            item.Padding = new Padding(6, 5, 6, 5);
    }

    private void ApplyTheme()
    {
        _renderer.LoadColors();
        _menu.Invalidate();
    }

    /// <summary>产品图标（tools/IconGen 生成），按系统小图标尺寸取最合适的一帧。</summary>
    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/ThreebrookInterpret;component/Assets/AppIcon.ico"));
        using var stream = resource!.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        ThemeManager.Changed -= ApplyTheme;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
