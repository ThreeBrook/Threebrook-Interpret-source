using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThreebrookInterpret.Views;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 开发用界面预览：ThreebrookInterpret.exe --render-previews 输出目录
/// 用示例数据创建各窗口，放在屏幕外渲染成 PNG（浅色、深色各一套）。
/// 不需要可见桌面、不操作鼠标键盘，也不读写用户的配置和历史。
/// </summary>
internal static class PreviewRenderer
{
    public static void Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var theme in new[] { "light", "dark" })
        {
            App.UsePreviewSettings(theme); // 设置窗口打开时会按配置重新应用外观，所以配置里也要写上
            App.Settings.Glossary.Add(new GlossaryEntry { Source = "三溪", Target = "Threebrook" });
            App.Settings.Glossary.Add(new GlossaryEntry { Source = "周报", Target = "weekly digest" });
            ThemeManager.Apply(theme);

            var translate = new TranslateWindow();
            translate.LoadPreview("The meeting has been moved to Friday afternoon because the client is traveling.", "→ 中文",
                Result("必应翻译", "会议已改到星期五下午，因为客户在旅行。", 1.0),
                Result("Yandex", "由于客户正在旅行，会议已转移到周五下午。", 0.4),
                Result("本地模型", "会议已改期至周五下午，因为客户正在出差。", 0.3),
                Failed("DeepL", "DeepL 返回 403：Forbidden"));
            Render(translate, outputDirectory, theme, "translate");

            var history = ClipboardHistory.CreatePreview();
            history.Entries.Add(new HistoryEntry { Text = "qwen3.8-27b", Time = DateTime.Now.AddMinutes(-3) });
            history.Entries.Add(new HistoryEntry { Text = "The quick brown fox jumps over the lazy dog. 这是一段比较长的文字，用来看看三行截断的效果，超过的部分会显示省略号。", Time = DateTime.Now.AddMinutes(-25) });
            history.Entries.Add(new HistoryEntry { Text = "D:\\Notes 2026", Time = DateTime.Now.AddDays(-1) });
            Render(new HistoryWindow(history), outputDirectory, theme, "history");

            var settings = new SettingsWindow { WindowStartupLocation = WindowStartupLocation.Manual };
            var pages = new[] { "general", "hotkeys", "engines", "glossary", "screenshot", "history", "about" };
            for (var i = 0; i < pages.Length; i++)
            {
                settings.NavList.SelectedIndex = i;
                Render(settings, outputDirectory, theme, "settings-" + pages[i], keepOpen: i < pages.Length - 1);
            }

            RenderElement(ScreenshotToolbar(), outputDirectory, theme, "screenshot-toolbar");
            RenderAnnotation(outputDirectory, theme);
            RenderSelection(outputDirectory, theme, "screenshot-narrow", new Rect(200, 60, 340, 200));
            RenderSelection(outputDirectory, theme, "screenshot-wide", new Rect(20, 40, 720, 300));
            RenderSelection(outputDirectory, theme, "screenshot-hover", new Rect(60, 40, 580, 320), hover: true);
        }
    }

    /// <summary>在一张合成的"屏幕"上演示全部标注工具：输出遮罩界面和导出结果。</summary>
    private static void RenderAnnotation(string directory, string theme)
    {
        var overlay = new ScreenshotOverlay(SyntheticScreen(760, 470), new System.Drawing.Rectangle(-30000, -30000, 760, 470))
        {
            PreviewMode = true,
            ShowActivated = false,
        };
        overlay.Show();
        DoEvents();
        var composed = overlay.RenderAnnotationPreview();
        DoEvents();
        Save(overlay, directory, theme, "screenshot-annotate");
        SaveBitmap(composed, directory, theme, "screenshot-annotate-export");
        overlay.Close();
    }

    private static void RenderSelection(string directory, string theme, string name, Rect selection, bool hover = false)
    {
        var overlay = new ScreenshotOverlay(SyntheticScreen(760, 470), new System.Drawing.Rectangle(-30000, -30000, 760, 470))
        {
            PreviewMode = true,
            ShowActivated = false,
        };
        overlay.Show();
        DoEvents();
        if (hover)
            overlay.PreviewHover(selection);
        else
            overlay.RenderSelectionPreview(selection);
        DoEvents();
        Save(overlay, directory, theme, name);
        overlay.Close();
    }

    private static BitmapSource SyntheticScreen(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF6)), null, new Rect(0, 0, width, 40));
            var face = new Typeface("Microsoft YaHei UI");
            var lines = new[]
            {
                "项目周会纪要  2026-10-23", "1. 发布时间：下周三 18:00（待确认）", "2. 客户演示改到周四下午", "3. 安装包体积控制在 1 MB 以内",
                "4. 截图标注：矩形、箭头、文字、马赛克", "联系人：张三 138-0000-0000", "备注：以上内容仅供内部参考",
            };
            for (var i = 0; i < lines.Length; i++)
                dc.DrawText(new FormattedText(lines[i], System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 18,
                    Brushes.Black, 1.0), new Point(80, 60 + i * 42));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xC1)), null, new Rect(450, 150, 180, 100));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SaveBitmap(BitmapSource bitmap, string directory, string theme, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, $"{theme}-{name}.png"));
        encoder.Save(file);
    }

    private static ResultItem Result(string name, string text, double seconds)
    {
        var item = new ResultItem(name);
        item.Complete(text, TimeSpan.FromSeconds(seconds));
        return item;
    }

    private static ResultItem Failed(string name, string message)
    {
        var item = new ResultItem(name);
        item.Fail(message);
        return item;
    }

    /// <summary>截图遮罩本身是全屏窗口，这里只渲染它的工具栏。</summary>
    private static FrameworkElement ScreenshotToolbar()
    {
        var overlay = new ScreenshotOverlay(new WriteableBitmap(4, 4, 96, 96, PixelFormats.Bgra32, null), new System.Drawing.Rectangle(0, 0, 4, 4));
        var toolbar = overlay.Toolbar;
        ((System.Windows.Controls.Panel)toolbar.Parent).Children.Remove(toolbar);
        toolbar.Visibility = Visibility.Visible;
        toolbar.Margin = new Thickness(16);
        return new System.Windows.Controls.Border
        {
            Child = toolbar,
            Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x68)),
        };
    }

    private static void Render(Window window, string directory, string theme, string name, bool keepOpen = false)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        window.Topmost = false;
        if (!window.IsVisible)
            window.Show();
        DoEvents();

        Save(window, directory, theme, name); // 含外圈阴影边距
        if (!keepOpen)
            window.Hide();
    }

    private static void RenderElement(FrameworkElement element, string directory, string theme, string name)
    {
        // 不在窗口里的元素要挂到应用资源下才能解析 DynamicResource
        var host = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight,
            Left = -30000, Top = -30000, Content = element,
        };
        host.Show();
        DoEvents();
        Save(element, directory, theme, name);
        host.Close();
    }

    private static void Save(FrameworkElement root, string directory, string theme, string name)
    {
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, $"{theme}-{name}.png"));
        encoder.Save(file);
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
