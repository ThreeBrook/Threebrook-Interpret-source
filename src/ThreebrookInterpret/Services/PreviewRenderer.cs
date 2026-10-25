using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
internal static partial class PreviewRenderer
{
    public static void Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        // 中文输出 light-xxx.png / dark-xxx.png，英文输出 en-light-xxx.png / en-dark-xxx.png
        foreach (var (lang, mode) in new[] { ("zh", "light"), ("zh", "dark"), ("en", "light"), ("en", "dark") })
        {
            var theme = lang == "zh" ? mode : "en-" + mode; // 文件名前缀
            App.UsePreviewSettings(mode); // 设置窗口打开时会按配置重新应用外观和语言，所以配置里也要写上
            App.Settings.Language = lang;
            App.Settings.Glossary.Add(new GlossaryEntry { Source = "三溪", Target = "Threebrook" });
            App.Settings.Glossary.Add(new GlossaryEntry { Source = "周报", Target = "weekly digest" });
            Loc.Apply(lang);
            SettingsStore.LocalizeDefaultEngineNames(App.Settings, Loc.IsEnglish);
            ThemeManager.Apply(mode);

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
                if (pages[i] == "screenshot")
                {
                    // 文字识别卡片在下半页，滚到底再出一张
                    settings.PageScroller.ScrollToEnd();
                    Render(settings, outputDirectory, theme, "settings-screenshot-ocr", keepOpen: true);
                    settings.PageScroller.ScrollToHome();
                }
            }

            RenderElement(ScreenshotToolbar(), outputDirectory, theme, "screenshot-toolbar");
            RenderAnnotation(outputDirectory, theme);
            var longShot = new LongScreenshotWindow(new System.Drawing.Rectangle(0, 0, 1080, 720));
            longShot.ShowPreviewState("正在拼接，继续向下滚动…", 4320);
            Render(longShot, outputDirectory, theme, "long-screenshot");
            longShot.Close();
            RenderSelection(outputDirectory, theme, "screenshot-narrow", new Rect(200, 60, 340, 200));
            RenderSelection(outputDirectory, theme, "screenshot-wide", new Rect(20, 40, 720, 300));
            RenderSelection(outputDirectory, theme, "screenshot-hover", new Rect(60, 40, 580, 320), hover: true);
            RenderPin(outputDirectory, theme);

            foreach (var page in new[] { "welcome", "options", "license", "progress", "done", "error", "cancelled" })
            {
                var setup = Setup.SetupWindow.CreatePreview(uninstall: false);
                setup.ShowPreviewPage(page);
                Render(setup, outputDirectory, theme, "setup-" + page);
                setup.Close();
            }
            foreach (var page in new[] { "confirm", "uprogress", "udone" })
            {
                var setup = Setup.SetupWindow.CreatePreview(uninstall: true);
                setup.ShowPreviewPage(page);
                Render(setup, outputDirectory, theme, "setup-" + page);
                setup.Close();
            }
        }
    }

    /// <summary>
    /// 识别对比：Windows OCR 与高精度识别（组件位置用环境变量 THREEBROOK_OCR_DIR 指定）各识别一次，
    /// 输出文字、耗时和每个字的位置。用默认配置，不读用户的 settings.json。
    /// </summary>
    public static void CompareOcr(string imagePath, string outputPath)
    {
        App.UsePreviewSettings("light");
        var image = BitmapFrame.Create(new Uri(Path.GetFullPath(imagePath)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        image.Freeze();
        var report = new System.Text.StringBuilder();
        foreach (var engine in new[] { "windows", "paddle" })
        {
            App.Settings.Screenshot.OcrEngine = engine;
            report.AppendLine($"== {engine}");
            try
            {
                for (var run = 0; run < 2; run++) // 第二次不含加载模型的时间
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var lines = OcrService.RecognizeLinesAsync(image).GetAwaiter().GetResult();
                    report.AppendLine($"-- run {run + 1}: {watch.ElapsedMilliseconds} ms, {lines.Count} lines{(engine == "paddle" ? " (" + PaddleOcr.LastTiming + ")" : "")}");
                    if (run == 1)
                        continue;
                    report.AppendLine(OcrService.ToText(lines));
                    foreach (var line in lines)
                        report.AppendLine("   " + string.Join(" ", line.Words.Select(w => $"{w.Text}@{w.Box.X:0}-{w.Box.Right:0}{(w.SpaceBefore == true ? "+sp" : "")}")));
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("failed: " + ex);
            }
        }
        File.WriteAllText(outputPath, report.ToString());
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

    /// <summary>
    /// 钉图选取文字：在合成截图上真正跑一遍 Windows OCR，选中第 2 行后半到第 3 行前半，
    /// 输出钉图和操作条，并把识别结果、选中文字、命中测试写进 pin-ocr.txt 方便核对。
    /// </summary>
    private static void RenderPin(string directory, string theme)
    {
        var image = new WriteableBitmap(new CroppedBitmap(SyntheticScreen(760, 470), new Int32Rect(60, 40, 420, 190)));
        image.Freeze();
        var report = new System.Text.StringBuilder();
        List<OcrLine> lines;
        try
        {
            lines = WindowsOcr.RecognizeLinesAsync(image, "auto").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, $"{theme}-pin-ocr.txt"), "OCR failed: " + ex.Message);
            return;
        }
        foreach (var line in lines)
            report.AppendLine($"{line.Bounds}  {OcrService.JoinWords(line.Words)}  [{string.Join("|", line.Words.Select(w => w.Text))}]");

        var pin = new PinWindow(image, -30000, -30000) { PreviewMode = true };
        // 第 2 行第 3 个词到第 3 行第 4 个词
        var from = lines.Count > 1 ? lines[0].Words.Count + 2 : 0;
        var to = lines.Count > 2 ? lines[0].Words.Count + lines[1].Words.Count + 3 : 3;
        var selected = pin.ShowPreviewSelection(lines, from, to);
        report.AppendLine().AppendLine($"selected [{from}, {to}]:").AppendLine(selected);
        report.AppendLine().AppendLine("hit tests (image px):");
        foreach (var point in new[] { new Point(30, 30), new Point(100, 64), new Point(5, 5), new Point(410, 180), new Point(100, 52) })
            report.AppendLine($"  {point}: wordAt={pin.PreviewWordAt(point)} nearest={pin.PreviewNearestWord(point)}");
        File.WriteAllText(Path.Combine(directory, $"{theme}-pin-ocr.txt"), report.ToString());

        Render(pin, directory, theme, "pin-select", keepOpen: true);
        var bar = pin.DetachTextActions();
        pin.Close();
        RenderElement(new System.Windows.Controls.Border
        {
            Child = bar,
            Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x68)),
            Padding = new Thickness(12),
        }, directory, theme, "pin-actions");
    }

    private static BitmapSource SyntheticScreen(int width, int height, double scale = 1)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF6)), null, new Rect(0, 0, width, 40));
            var face = new Typeface("Microsoft YaHei UI");
            var lines = new[]
            {
                "项目周会纪要  2026-10-23", "1. 发布时间：下周三 18:00（待确认）", "2. 客户演示改到周四下午", "3. 截图识别改用高精度引擎",
                "4. 截图标注：矩形、箭头、文字、马赛克", "联系人：张三 138-0000-0000", "备注：以上内容仅供内部参考",
            };
            for (var i = 0; i < lines.Length; i++)
                dc.DrawText(new FormattedText(lines[i], System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 18,
                    Brushes.Black, scale), new Point(80, 60 + i * 42));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xC1)), null, new Rect(450, 150, 180, 100));
        }
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); // 宣传图用 2 倍
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
