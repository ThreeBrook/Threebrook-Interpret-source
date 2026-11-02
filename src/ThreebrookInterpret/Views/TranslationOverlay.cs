using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Views;

/// <summary>
/// 原位翻译的图层：每块文字用周围的背景色盖住，再用原文的颜色把译文排进原来的范围，放不下就缩小字号。
/// 画布按原图像素大小，坐标直接用 OCR 给的像素坐标。
/// </summary>
internal static class TranslationOverlay
{
    /// <summary>字号最小缩到原文行高的这个比例（先用下面的空白，实在放不下才缩到这么小）；再放不下就把底色往下延长。</summary>
    private const double MinFontRatio = 0.4;

    /// <summary>译文可以往下多占的高度（行高的倍数），不超过下面一块文字。</summary>
    private const double ExtraBelow = 1.5;

    public static void Draw(Canvas canvas, BitmapSource image, IReadOnlyList<TextRegion> regions, FontFamily font)
    {
        canvas.Children.Clear();
        var pixels = new Pixels(image);
        var translated = regions.Where(r => !string.IsNullOrWhiteSpace(r.Translation)).ToList();
        // 识别框高度上下浮动几个像素：高度相近的块取同一个字号，同一级文字（表格各格、正文各段）大小一致
        var heights = translated.Select(r => r.LineHeight).OrderBy(h => h).ToList();
        double Snap(double h)
        {
            var near = heights.Where(x => Math.Abs(x - h) <= h * 0.15).ToList();
            return near[near.Count / 2];
        }
        foreach (var region in translated)
        {
            var height = region.LineHeight;
            var pad = Math.Max(2, height * 0.15);
            var area = region.Bounds;
            area.Inflate(pad, pad);
            var background = pixels.Background(area);
            var foreground = pixels.Foreground(region.Lines, background);

            // 只有一行的块（标签、按钮文字）译文常比原文长：允许向右伸到下一块或图片边缘
            var width = area.Width;
            if (region.Lines.Count == 1)
                width = Math.Max(width, pixels.ClearRight(area, background, RoomToRight(region, translated, image.PixelWidth)) - area.Left);

            var text = new TextBlock
            {
                Text = region.Translation,
                FontFamily = font,
                Foreground = new SolidColorBrush(foreground),
                TextWrapping = TextWrapping.Wrap,
                Width = width,
            };
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
            // 先用下面的空白排开，放不下再缩小字号
            var below = pixels.ClearBelow(area with { Width = width }, background, RoomBelow(region, translated, image.PixelHeight));
            var maxHeight = Math.Min(area.Height + height * ExtraBelow, below - area.Top);
            Fit(text, region, Math.Max(area.Height, maxHeight), Snap(height), width);

            var size = text.DesiredSize;
            if (region.Lines.Count == 1 && text.TextWrapping == TextWrapping.NoWrap)
            {
                // 不换行放得下：底色只盖到译文末尾（至少盖住原文）
                text.Width = double.NaN;
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width = Math.Max(area.Width, Math.Min(width, text.DesiredSize.Width + 1));
                text.Width = width;
                size = new Size(width, text.DesiredSize.Height);
            }
            var cover = new Rect(area.X, area.Y, width, Math.Max(area.Height, size.Height));
            var block = new Border
            {
                Width = cover.Width,
                Height = cover.Height,
                Background = new SolidColorBrush(background),
                Child = text,
            };
            text.VerticalAlignment = VerticalAlignment.Center;
            Canvas.SetLeft(block, cover.X);
            Canvas.SetTop(block, cover.Y);
            canvas.Children.Add(block);
        }
    }

    /// <summary>从原文行高对应的字号开始，排不下就逐步缩小；单行块先试不换行。</summary>
    private static void Fit(TextBlock text, TextRegion region, double maxHeight, double lineHeight, double width)
    {
        var size = lineHeight * 0.78; // 识别框比字的字面略高
        var min = Math.Max(6, lineHeight * MinFontRatio);
        while (true)
        {
            text.FontSize = size;
            if (region.Lines.Count == 1)
            {
                // 量不换行的自然宽度要先去掉固定宽度，否则量出来的总是 Width
                text.Width = double.NaN;
                text.TextWrapping = TextWrapping.NoWrap;
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                text.Width = width;
                if (text.DesiredSize.Width <= width)
                {
                    text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    return;
                }
            }
            text.TextWrapping = TextWrapping.Wrap;
            text.Measure(new Size(width, double.PositiveInfinity));
            if (text.DesiredSize.Height <= maxHeight + lineHeight * 0.1 || size <= min)
                return;
            size = Math.Max(min, size * 0.92);
        }
    }

    /// <summary>这块下面能用到哪里：正下方最近一块的上边，或图片下边缘。</summary>
    private static double RoomBelow(TextRegion region, IEnumerable<TextRegion> all, int imageHeight)
    {
        var bounds = region.Bounds;
        var bottom = (double)imageHeight;
        foreach (var other in all)
        {
            var b = other.Bounds;
            if (other != region && b.Top >= bounds.Bottom && b.Left < bounds.Right && b.Right > bounds.Left)
                bottom = Math.Min(bottom, b.Top - region.LineHeight * 0.15);
        }
        return bottom;
    }

    /// <summary>这块右边能用到哪里：同一排右侧最近一块的左边，或图片右边缘。</summary>
    private static double RoomToRight(TextRegion region, IEnumerable<TextRegion> all, int imageWidth)
    {
        var bounds = region.Bounds;
        var right = (double)imageWidth;
        foreach (var other in all)
        {
            var b = other.Bounds;
            if (other != region && b.Left >= bounds.Right && b.Top < bounds.Bottom && b.Bottom > bounds.Top)
                right = Math.Min(right, b.Left - bounds.Height * 0.3);
        }
        return right;
    }

    /// <summary>原图像素（统一转成 BGRA），取背景色和文字颜色用。</summary>
    private sealed class Pixels
    {
        private readonly byte[] _data;
        private readonly int _width, _height;

        public Pixels(BitmapSource image)
        {
            var bgra = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            _width = bgra.PixelWidth;
            _height = bgra.PixelHeight;
            _data = new byte[_width * _height * 4];
            bgra.CopyPixels(_data, _width * 4, 0);
        }

        private Color At(int x, int y)
        {
            var i = (Math.Clamp(y, 0, _height - 1) * _width + Math.Clamp(x, 0, _width - 1)) * 4;
            return Color.FromRgb(_data[i + 2], _data[i + 1], _data[i]);
        }

        /// <summary>从 area 右边往右，到 limit 为止背景色一直不变的位置（遇到别的文字、图形、分栏的底色就停）。</summary>
        public double ClearRight(Rect area, Color background, double limit)
        {
            var rows = new[] { area.Top + 1, area.Top + area.Height / 2, area.Bottom - 1 };
            var x = (int)Math.Ceiling(area.Right);
            for (; x < limit; x++)
                if (rows.Any(y => !Near(At(x, (int)y), background)))
                    return Math.Max(area.Right, x - 2);
            return Math.Max(area.Right, limit);
        }

        /// <summary>从 area 下边往下，到 limit 为止整行都是背景色的位置。</summary>
        public double ClearBelow(Rect area, Color background, double limit)
        {
            var step = Math.Max(1, (int)(area.Width / 60));
            var y = (int)Math.Ceiling(area.Bottom);
            for (; y < limit && y < _height; y++)
                for (var x = (int)area.Left; x < (int)area.Right; x += step)
                    if (!Near(At(x, y), background))
                        return Math.Max(area.Bottom, y - 2);
            return Math.Max(area.Bottom, Math.Min(limit, _height));
        }

        private static bool Near(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 36;

        /// <summary>背景色：盖住范围外面一圈像素的中位数（各通道分别取）。</summary>
        public Color Background(Rect area)
        {
            var ring = new List<Color>();
            int left = (int)Math.Floor(area.Left) - 1, right = (int)Math.Ceiling(area.Right),
                top = (int)Math.Floor(area.Top) - 1, bottom = (int)Math.Ceiling(area.Bottom);
            var step = Math.Max(1, (right - left + bottom - top) / 200);
            for (var x = left; x <= right; x += step)
            {
                ring.Add(At(x, top));
                ring.Add(At(x, bottom));
            }
            for (var y = top; y <= bottom; y += step)
            {
                ring.Add(At(left, y));
                ring.Add(At(right, y));
            }
            return Color.FromRgb(Median(ring, c => c.R), Median(ring, c => c.G), Median(ring, c => c.B));
        }

        /// <summary>文字颜色：文字范围里和背景差得最多的那些像素的平均色；差别太小（识别框里几乎没字）就用黑或白。</summary>
        public Color Foreground(IEnumerable<Rect> lines, Color background)
        {
            var samples = new List<(Color Color, int Distance)>();
            foreach (var line in lines)
            {
                var step = Math.Max(1, (int)(line.Width * line.Height / 4000));
                for (var y = (int)line.Top; y < (int)line.Bottom; y++)
                    for (var x = (int)line.Left; x < (int)line.Right; x += step)
                    {
                        var c = At(x, y);
                        samples.Add((c, Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B)));
                    }
            }
            var max = samples.Count > 0 ? samples.Max(s => s.Distance) : 0;
            if (max < 90)
                return Luminance(background) > 0.5 ? Colors.Black : Colors.White;
            var strong = samples.Where(s => s.Distance >= max * 0.7).Select(s => s.Color).ToList();
            return Color.FromRgb((byte)strong.Average(c => c.R), (byte)strong.Average(c => c.G), (byte)strong.Average(c => c.B));
        }

        private static byte Median(List<Color> colors, Func<Color, byte> channel) =>
            colors.Select(channel).OrderBy(v => v).ElementAt(colors.Count / 2);

        private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
    }
}
