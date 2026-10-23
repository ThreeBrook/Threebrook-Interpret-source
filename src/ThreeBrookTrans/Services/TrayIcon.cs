using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ThreeBrookTrans.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
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
        var menu = new ContextMenuStrip();
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

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "ThreeBrook Trans",
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

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>运行时绘制托盘图标，免去单独的 .ico 资源文件。</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var background = new SolidBrush(Color.FromArgb(0x1F, 0x6F, 0xEB));
            g.FillPath(background, path);

            using var font = new Font("Segoe UI", 17, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("T", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }

        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
