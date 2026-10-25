using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ThreebrookInterpret.Translation;
using ThreebrookInterpret.Views;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 开发用宣传图：ThreebrookInterpret.exe --render-promo 输出目录
/// 真实窗口（示例数据）摆在三溪品牌色背景上，2 倍分辨率输出 2880×1800 PNG。
/// 每个场景出两张：带标题文案的（左侧文案、右侧画面）和不带文案的（-clean，画面居中，留给设计排版）。
/// 中英文 × 浅色深色各一套。高精度识别组件用环境变量 THREEBROOK_OCR_DIR 指定时，钉图场景用 PaddleOCR 识别。
/// </summary>
internal static partial class PreviewRenderer
{
    private const double PromoWidth = 1440, PromoHeight = 900, PromoScale = 2;

    /// <summary>截下来的画面：2 倍位图 + 设备无关尺寸。</summary>
    private sealed record Shot(BitmapSource Image, double Width, double Height);

    private sealed record Placed(Shot Shot, double X, double Y);

    private sealed record Scene(string Name, string Title, string Subtitle, List<Placed> Items);

    public static void RunPromo(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var translateShots = new Dictionary<(string Lang, string Mode), Shot>();
        foreach (var (lang, mode) in new[] { ("zh", "light"), ("zh", "dark"), ("en", "light"), ("en", "dark") })
        {
            App.UsePreviewSettings(mode);
            App.Settings.Language = lang;
            Loc.Apply(lang);
            SettingsStore.LocalizeDefaultEngineNames(App.Settings, Loc.IsEnglish);
            ThemeManager.Apply(mode);
            var zh = lang == "zh";

            var translate = TranslateScene(zh, mode);
            translateShots[(lang, mode)] = translate.Items[^1].Shot;
            foreach (var scene in new[] { translate, PinScene(zh), AnnotateScene(zh), HistoryScene(zh), EnginesScene(zh) })
                SavePromo(scene, lang, mode, outputDirectory);
        }

        // 浅色深色并排：两种外观的翻译窗口放在一起，背景浅色、深色各出一张
        foreach (var lang in new[] { "zh", "en" })
        {
            Loc.Apply(lang); // 文案里的产品名跟着语言
            var zh = lang == "zh";
            var light = translateShots[(lang, "light")];
            var dark = translateShots[(lang, "dark")];
            var scene = new Scene("themes",
                zh ? "浅色深色，中英双语" : "Light or dark, Chinese or English",
                zh ? "跟随 Windows 的外观和语言自动切换，也可以在设置里自己选。" : "Follows your Windows theme and language, or pick your own in Settings.",
                [new Placed(light, 0, 0), new Placed(dark, light.Width - 120, 90)]);
            foreach (var mode in new[] { "light", "dark" })
            {
                ThemeManager.Apply(mode);
                SavePromo(scene, lang, mode, outputDirectory);
            }
        }
    }

    // —— 场景 ——

    private static Scene TranslateScene(bool zh, string mode)
    {
        Shot document;
        var translate = new TranslateWindow();
        if (zh)
        {
            document = CaptureElement(DocumentWindow("Project notes.txt", mode, 600,
                [("Hi team,", false)],
                [("The meeting has been moved to Friday afternoon because the client is traveling.", true),
                 (" Please update your calendars and bring the latest draft of the proposal.", false)],
                [("Thanks, and see you on Friday!", false)]));
            translate.LoadPreview("The meeting has been moved to Friday afternoon because the client is traveling.", "→ " + Languages.Get("zh").DisplayName,
                Result("必应翻译", "会议已改到星期五下午，因为客户在出差。", 0.6),
                Result("Yandex", "由于客户正在出差，会议已改到周五下午。", 0.4),
                Result("DeepL", "由于客户出差，会议改到了周五下午。", 0.5),
                Result("本地大模型", "会议已改期至周五下午，因为客户正在出差。", 1.1));
        }
        else
        {
            document = CaptureElement(DocumentWindow("周会纪要.txt", mode, 600,
                [("各位好：", false)],
                [("由于客户出差，会议改到周五下午。", true), ("请大家更新日程，并带上方案的最新稿。", false)],
                [("谢谢，周五见！", false)]));
            translate.LoadPreview("由于客户出差，会议改到周五下午。", "→ " + Languages.Get("en").DisplayName,
                Result("Bing", "Because the client is on a business trip, the meeting has been moved to Friday afternoon.", 0.6),
                Result("Yandex", "Due to the client's business trip, the meeting was moved to Friday afternoon.", 0.4),
                Result("DeepL", "The meeting has been moved to Friday afternoon as the client is traveling.", 0.5),
                Result("Local model", "The meeting is now on Friday afternoon, since the client is away on business.", 1.1));
        }
        var window = CaptureWindow(translate);
        translate.Close();
        return new Scene("translate",
            zh ? "选中文字，按一下就翻" : "Select text. Press a key. Done.",
            zh ? "多个引擎的结果并排对照：必应、Yandex、DeepL、本地大模型……Alt+D 划词翻译，Alt+Q 输入翻译。"
               : "Results from several engines side by side: Bing, Yandex, DeepL, your local model. Alt+D for selected text, Alt+Q to type.",
            [new Placed(document, 0, 0), new Placed(window, 330, 150)]);
    }

    private static Scene PinScene(bool zh)
    {
        var image = PromoNotes();
        var lines = OcrService.RecognizeLinesAsync(image).GetAwaiter().GetResult();
        // 选中第 2、3 条（"2." 开头的那行和下一行）
        var first = Math.Max(0, lines.FindIndex(l => OcrService.JoinWords(l.Words).StartsWith('2')));
        var last = Math.Min(lines.Count - 1, first + 1);
        var from = lines.Take(first).Sum(l => l.Words.Count);
        var to = lines.Take(last + 1).Sum(l => l.Words.Count) - 1;

        var pin = new PinWindow(image, -30000, -30000, zoom: 1 / PromoScale) { PreviewMode = true };
        pin.Show();
        DoEvents();
        var selected = pin.ShowPreviewSelection(lines, from, to);
        DoEvents();
        var pinShot = CaptureElement(Framed(CaptureWindow(pin), 2));
        var selection = pin.PreviewSelectionBounds;
        var bar = CaptureElement(pin.DetachTextActions());
        pin.Close();

        // 截图是中文纪要，中英文界面都演示译成英文
        var translate = new TranslateWindow();
        translate.LoadPreview(selected, "→ " + Languages.Get("en").DisplayName,
            Result(zh ? "必应翻译" : "Bing", "2. The customer demo has been moved to Thursday at 3 p.m.\n3. Screenshot OCR now uses the high-accuracy engine", 0.5),
            Result(zh ? "本地大模型" : "Local model", "2. Client demo rescheduled to Thursday, 3 p.m.\n3. Screenshot text recognition switches to the high-accuracy engine", 1.2));
        var window = CaptureWindow(translate);
        translate.Close();

        // Framed 四周留了 FrameMargin 的阴影边距，操作条放在选中文字正下方（它自己也带阴影边距）
        var barX = FrameMargin + selection.Left - 4;
        var barY = FrameMargin + selection.Bottom - 2;
        return new Scene("pin",
            zh ? "钉在桌面，直接选字" : "Pin it, then select the text",
            zh ? "截图钉住后自动识别文字，拖动就能选取、复制、翻译。内置高精度识别，在本机完成、不联网。"
               : "Pinned screenshots are recognized automatically: drag across the text to copy or translate it. High-accuracy OCR runs on your computer, offline.",
            [new Placed(pinShot, 0, 0), new Placed(bar, barX, barY), new Placed(window, pinShot.Width - 150, pinShot.Height - 40)]);
    }

    private static Scene AnnotateScene(bool zh)
    {
        var overlay = new ScreenshotOverlay(SyntheticScreen(760, 470, PromoScale), new System.Drawing.Rectangle(-30000, -30000, 760, 470))
        {
            PreviewMode = true,
            ShowActivated = false,
        };
        overlay.Show();
        DoEvents();
        overlay.RenderAnnotationPreview();
        DoEvents();
        var shot = CaptureElement(Framed(CaptureWindow(overlay), 10));
        overlay.Close();
        return new Scene("annotate",
            zh ? "截图、标注一步到位" : "Capture and annotate in one go",
            zh ? "矩形、箭头、画笔、荧光笔、文字、马赛克、序号；复制、保存、钉图都带标注，还能滚动长截图。"
               : "Boxes, arrows, pens, highlighters, text, mosaic and numbers. Copy, save or pin with your marks, or take scrolling screenshots.",
            [new Placed(shot, 0, 0)]);
    }

    private static Scene HistoryScene(bool zh)
    {
        var history = ClipboardHistory.CreatePreview();
        var now = DateTime.Now;
        var samples = zh
            ? new[] { "会议改到周五下午 3 点，地点不变。", "https://interpret.threebrook.com/", "The quick brown fox jumps over the lazy dog.", "D:\\Projects\\2027 规划", "下周三 18:00 发布（待确认）" }
            : new[] { "The meeting moved to Friday, 3 p.m. Same room.", "https://interpret.threebrook.com/", "会议改到周五下午 3 点，地点不变。", "D:\\Projects\\2027 plan", "Release: next Wednesday 18:00 (TBC)" };
        var ages = new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(25), TimeSpan.FromHours(2), TimeSpan.FromHours(20) };
        for (var i = 0; i < samples.Length; i++)
            history.Entries.Add(new HistoryEntry { Text = samples[i], Time = now - ages[i] });
        var window = new HistoryWindow(history);
        var shot = CaptureWindow(window);
        window.Close();
        return new Scene("history",
            zh ? "剪贴板历史，随时找回" : "Never lose what you copied",
            zh ? "复制过的文字和图片都记在本机，Alt+H 呼出，回车直接粘贴。"
               : "Text and images you copy are kept on your computer. Press Alt+H, pick one, and hit Enter to paste.",
            [new Placed(shot, 0, 0)]);
    }

    private static Scene EnginesScene(bool zh)
    {
        // 8 种引擎都列出来（示例），选中本地大模型，展示接口地址和模型
        EngineConfig Engine(string type, string zhName, string enName, bool enabled) =>
            new() { Id = type, Type = type, Name = zh ? zhName : enName, Enabled = enabled };
        var local = Engine("openai", "本地大模型", "Local model", true);
        local.BaseUrl = "http://localhost:11434/v1";
        local.Model = "qwen3:8b";
        App.Settings.Engines =
        [
            Engine("bing", "必应翻译", "Bing", true), Engine("yandex", "Yandex", "Yandex", true), Engine("deepl", "DeepL", "DeepL", true),
            local, Engine("baidu", "百度翻译", "Baidu", false), Engine("youdao", "有道翻译", "Youdao", false),
            Engine("tencent", "腾讯翻译", "Tencent", false), Engine("azure", "Azure 翻译", "Azure Translator", false),
        ];
        var settings = new SettingsWindow { WindowStartupLocation = WindowStartupLocation.Manual };
        settings.NavList.SelectedIndex = 2; // 翻译引擎
        settings.EnginesList.SelectedIndex = 3;
        var shot = CaptureWindow(settings);
        settings.Close();
        return new Scene("engines",
            zh ? "8 种引擎，也能接本地大模型" : "8 engines, or your own local model",
            zh ? "必应、Yandex 免费即用；DeepL、百度、有道、腾讯、Azure 填上 Key 就能用；OpenAI 兼容接口接本地大模型，保密内容不出电脑。"
               : "Bing and Yandex work for free. Add a key for DeepL, Baidu, Youdao, Tencent or Azure. Connect a local model through an OpenAI-compatible API, so private text stays on your computer.",
            [new Placed(shot, 0, 0)]);
    }

    // —— 素材 ——

    /// <summary>钉图用的"会议纪要"截图，2 倍分辨率。</summary>
    private static BitmapSource PromoNotes()
    {
        const double width = 470, height = 220;
        var lines = new[] { "项目周会纪要  2026-10-23", "1. 发布时间：下周三 18:00（待确认）", "2. 客户演示改到周四下午 3 点", "3. 截图识别改用高精度引擎", "4. 下次例会：11 月 6 日" };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var face = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var bold = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            for (var i = 0; i < lines.Length; i++)
                dc.DrawText(new FormattedText(lines[i], System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, i == 0 ? bold : face, i == 0 ? 17 : 15,
                    new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)), PromoScale), new Point(24, 22 + i * 38));
        }
        var bitmap = new RenderTargetBitmap((int)(width * PromoScale), (int)(height * PromoScale), 96 * PromoScale, 96 * PromoScale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>示意用的文档窗口（不是真实程序），选中的句子用系统选中色标出来。</summary>
    private static FrameworkElement DocumentWindow(string title, string mode, double width, params (string Text, bool Selected)[][] paragraphs)
    {
        var dark = mode == "dark";
        SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
        var text = Brush(dark ? "#E3E6E6" : "#1F2328");
        var body = new StackPanel { Margin = new Thickness(34, 26, 34, 30) };
        foreach (var paragraph in paragraphs)
        {
            var block = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 15.5, LineHeight = 27, Margin = new Thickness(0, 0, 0, 12), Foreground = text,
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            };
            foreach (var (segment, selected) in paragraph)
                block.Inlines.Add(new Run(segment) { Background = selected ? Brush(dark ? "#2C5A8A" : "#BFDBFA") : null });
            body.Children.Add(block);
        }
        var titleBar = new DockPanel { Height = 38, Background = Brush(dark ? "#262B2C" : "#F2F3F3") };
        var buttons = new TextBlock
        {
            Text = "—     ▢     ✕", FontSize = 11, Foreground = Brush(dark ? "#9AA4A3" : "#6B7280"), Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI"),
        };
        DockPanel.SetDock(buttons, Dock.Right);
        titleBar.Children.Add(buttons);
        titleBar.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12.5, Foreground = Brush(dark ? "#C9D1D0" : "#3D4450"), Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
        });
        var content = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        content.Children.Add(titleBar);
        content.Children.Add(body);
        return new Border
        {
            Width = width, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Brush(dark ? "#1C2122" : "#FFFFFF"),
            BorderBrush = Brush(dark ? "#333B3C" : "#DADDE0"), BorderThickness = new Thickness(1), Child = content,
            Margin = new Thickness(FrameMargin), Effect = Shadow(),
        };
    }

    private const double FrameMargin = 36;

    private static DropShadowEffect Shadow() => new() { BlurRadius = 46, ShadowDepth = 10, Direction = 270, Opacity = 0.22, Color = Colors.Black };

    /// <summary>把截下的画面加圆角和阴影（截图遮罩、钉图本身没有窗口阴影）。</summary>
    private static FrameworkElement Framed(Shot shot, double radius) => new Border
    {
        Width = shot.Width, Height = shot.Height, CornerRadius = new CornerRadius(radius),
        Background = new ImageBrush(shot.Image) { Stretch = Stretch.Fill }, Margin = new Thickness(FrameMargin), Effect = Shadow(),
    };

    // —— 截取与合成 ——

    private static Shot CaptureWindow(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        window.Topmost = false;
        if (!window.IsVisible)
            window.Show();
        DoEvents();
        return Capture(window);
    }

    private static Shot CaptureElement(FrameworkElement element)
    {
        // 不在窗口里的元素要挂到窗口下才能解析 DynamicResource、完成排版。
        // 外面再包一层：元素自己的 Margin（阴影边距）不算在它的 ActualWidth 里，直接截会把阴影和底边截掉
        var wrapper = new Border { Child = element };
        var host = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight, Left = -30000, Top = -30000, Content = wrapper,
        };
        host.Show();
        DoEvents();
        var shot = Capture(wrapper);
        wrapper.Child = null;
        host.Content = null;
        host.Close();
        return shot;
    }

    private static Shot Capture(FrameworkElement root)
    {
        root.UpdateLayout();
        double width = root.ActualWidth, height = root.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * PromoScale), (int)Math.Ceiling(height * PromoScale),
            96 * PromoScale, 96 * PromoScale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return new Shot(bitmap, width, height);
    }

    private static void SavePromo(Scene scene, string lang, string mode, string directory)
    {
        foreach (var captioned in new[] { true, false })
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                DrawBackdrop(dc, mode == "dark");
                // 画面整体缩放到可用区域里：带文案时在右侧，不带时居中
                var bounds = scene.Items.Select(i => new Rect(i.X, i.Y, i.Shot.Width, i.Shot.Height)).Aggregate(Rect.Union);
                var area = captioned ? new Rect(560, 40, 850, 820) : new Rect(70, 40, 1300, 820);
                var s = Math.Min(1, Math.Min(area.Width / bounds.Width, area.Height / bounds.Height));
                var ox = area.X + (area.Width - bounds.Width * s) / 2 - bounds.X * s;
                var oy = area.Y + (area.Height - bounds.Height * s) / 2 - bounds.Y * s;
                foreach (var item in scene.Items)
                    dc.DrawImage(item.Shot.Image, new Rect(ox + item.X * s, oy + item.Y * s, item.Shot.Width * s, item.Shot.Height * s));
                if (captioned)
                    DrawCaption(dc, scene, lang == "zh", mode == "dark");
            }
            var bitmap = new RenderTargetBitmap((int)(PromoWidth * PromoScale), (int)(PromoHeight * PromoScale), 96 * PromoScale, 96 * PromoScale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, $"{lang}-{mode}-{scene.Name}{(captioned ? "" : "-clean")}.png"));
            encoder.Save(file);
        }
    }

    /// <summary>品牌背景：米白 / 墨绿底，右上溪青、左下暮紫两团柔光（三溪外观规范的品牌色和产品色）。</summary>
    private static void DrawBackdrop(DrawingContext dc, bool dark)
    {
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        dc.DrawRectangle(new LinearGradientBrush(C(dark ? "#0F1A19" : "#F4F1E8"), C(dark ? "#132E2A" : "#E4F0EC"), 35), null,
            new Rect(0, 0, PromoWidth, PromoHeight));
        void Glow(string hex, byte alpha, Point center, double radius)
        {
            var color = C(hex);
            var brush = new RadialGradientBrush(Color.FromArgb(alpha, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B));
            dc.DrawEllipse(brush, null, center, radius, radius);
        }
        Glow("#4CC0B6", dark ? (byte)70 : (byte)80, new Point(1250, 110), 560);
        Glow("#7460C4", dark ? (byte)75 : (byte)45, new Point(170, 830), 520);
    }

    private static void DrawCaption(DrawingContext dc, Scene scene, bool zh, bool dark)
    {
        Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var body = new FontFamily("Microsoft YaHei UI");
        var display = zh ? body : (FontFamily)Application.Current.FindResource("F.DisplayEn");
        const double left = 88, maxWidth = 440;

        var eyebrow = new FormattedText(AppInfo.FullName, culture, FlowDirection.LeftToRight,
            new Typeface(body, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 17, B(dark ? "#4CC0B6" : "#1C8780"), PromoScale);
        var title = new FormattedText(scene.Title, culture, FlowDirection.LeftToRight,
            new Typeface(display, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), zh ? 42 : 44, B(dark ? "#F4F1E8" : "#0F1A19"), PromoScale)
        {
            MaxTextWidth = maxWidth, LineHeight = zh ? 58 : 54,
        };
        var subtitle = new FormattedText(scene.Subtitle, culture, FlowDirection.LeftToRight,
            new Typeface(body, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 18, B(dark ? "#A9BCB8" : "#4A5A57"), PromoScale)
        {
            MaxTextWidth = maxWidth, LineHeight = 31,
        };

        const double icon = 40, gap1 = 26, gap2 = 22;
        var total = icon + gap1 + title.Height + gap2 + subtitle.Height;
        var y = (PromoHeight - total) / 2;
        var logo = new BitmapImage(new Uri("pack://application:,,,/ThreebrookInterpret;component/Assets/AppIcon-128.png"));
        dc.DrawImage(logo, new Rect(left, y, icon, icon));
        dc.DrawText(eyebrow, new Point(left + icon + 14, y + (icon - eyebrow.Height) / 2));
        y += icon + gap1;
        dc.DrawText(title, new Point(left, y));
        y += title.Height + gap2;
        dc.DrawText(subtitle, new Point(left, y));
    }
}
