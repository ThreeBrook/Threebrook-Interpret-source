using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 三溪通译（Threebrook Interpret）产品图标（三溪外观规范 §8）：
// 圆角方形 + 产品色（暮紫 #7460C4）渐变 + 白色主图案"文A"；
// 64 像素及以上左下角带月亮签名，48 像素简化签名（小圆月），32 像素及以下不带。
//
// 运行：dotnet run --project tools/IconGen
// 输出：src/ThreeBrookTrans/Assets/AppIcon.ico、AppIcon-36.png（标题栏 18 DIP @2x）、AppIcon-128.png（关于页）
internal static class Program
{
    private static readonly Color GradientTop = Color.FromRgb(0x86, 0x74, 0xD4);
    private static readonly Color GradientBottom = Color.FromRgb(0x5E, 0x4A, 0xAE);
    private static readonly Color Moon = Color.FromRgb(0xF4, 0xC2, 0x5B);

    private static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    [STAThread]
    private static void Main()
    {
        var root = FindRepoRoot();
        var assets = Path.Combine(root, "src", "ThreeBrookTrans", "Assets");
        var fonts = Path.Combine(root, "src", "ThreeBrookTrans", "Fonts");
        Directory.CreateDirectory(assets);
        var latin = new FontFamily(new Uri(fonts + Path.DirectorySeparatorChar), "./#Bricolage Grotesque");

        var frames = new List<(int Size, BitmapSource Image)>();
        foreach (var size in IcoSizes)
            frames.Add((size, Render(size, latin)));

        WriteIco(Path.Combine(assets, "AppIcon.ico"), frames);
        SavePng(Render(36, latin), Path.Combine(assets, "AppIcon-36.png"));
        SavePng(Render(128, latin), Path.Combine(assets, "AppIcon-128.png"));

        // 预览图：各尺寸并排，便于检查小尺寸是否清楚
        SavePng(Preview(frames), Path.Combine(root, "tools", "IconGen", "preview.png"));
        Console.WriteLine("icons written to " + assets);
    }

    private static BitmapSource Render(int size, FontFamily latin)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double s = size;
            var radius = s * 0.225;
            var gradient = new LinearGradientBrush(GradientTop, GradientBottom, new Point(0, 0), new Point(1, 1));
            dc.DrawRoundedRectangle(gradient, null, new Rect(0, 0, s, s), radius, radius);

            // 小尺寸图案放大一些、去掉签名，保证 16 像素也能认出
            var small = size <= 24;
            var zhSize = s * (small ? 0.62 : 0.50);
            var enSize = s * (small ? 0.50 : 0.42);
            var zhOrigin = small ? new Point(s * 0.05, s * 0.00) : new Point(s * 0.14, s * 0.10);
            var enOrigin = small ? new Point(s * 0.52, s * 0.40) : new Point(s * 0.52, s * 0.40);

            var zh = Text("文", new FontFamily("Microsoft YaHei UI"), FontWeights.Bold, zhSize, s);
            var en = Text("A", latin, FontWeights.Bold, enSize, s);
            var zhGeometry = zh.BuildGeometry(zhOrigin);
            var enGeometry = en.BuildGeometry(enOrigin);

            // "A" 外圈描一道底色，叠在"文"上时两字分得开
            var gap = new Pen(new SolidColorBrush(Color.FromRgb(0x6A, 0x57, 0xBA)), Math.Max(1, s * 0.045)) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(Brushes.White, null, zhGeometry);
            dc.DrawGeometry(null, gap, enGeometry);
            dc.DrawGeometry(Brushes.White, null, enGeometry);

            if (size >= 64)
            {
                // 月亮签名：月牙，左下角
                var r = s * 0.075;
                var center = new Point(s * 0.17, s * 0.83);
                var crescent = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(center, r, r),
                    new EllipseGeometry(new Point(center.X + r * 0.55, center.Y - r * 0.35), r * 0.85, r * 0.85));
                dc.DrawGeometry(new SolidColorBrush(Moon), null, crescent);
            }
            else if (size == 48)
            {
                var r = s * 0.06;
                dc.DrawEllipse(new SolidColorBrush(Moon), null, new Point(s * 0.17, s * 0.83), r, r);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static FormattedText Text(string text, FontFamily family, FontWeight weight, double emSize, double canvas) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
            emSize, Brushes.White, 1.0) { TextAlignment = TextAlignment.Left };

    private static BitmapSource Preview(List<(int Size, BitmapSource Image)> frames)
    {
        var visual = new DrawingVisual();
        double x = 16, height = 256 + 32;
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF6, 0xFA, 0xF6)), null, new Rect(0, 0, 1000, height * 2));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0F, 0x1A, 0x19)), null, new Rect(0, height, 1000, height));
            foreach (var (size, image) in frames)
            {
                dc.DrawImage(image, new Rect(x, 16, size, size));
                dc.DrawImage(image, new Rect(x, height + 16, size, size));
                x += size + 16;
            }
        }
        var bitmap = new RenderTargetBitmap((int)x, (int)height * 2, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>写多尺寸 ICO：64 像素及以下用 32 位 BMP（兼容 System.Drawing / 托盘），更大的用 PNG。</summary>
    private static void WriteIco(string path, List<(int Size, BitmapSource Image)> frames)
    {
        var payloads = new List<byte[]>();
        foreach (var (size, image) in frames)
            payloads.Add(size <= 64 ? Dib(image) : Png(image));

        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)frames.Count);
        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            var size = frames[i].Size;
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(payloads[i].Length);
            w.Write(offset);
            offset += payloads[i].Length;
        }
        foreach (var payload in payloads)
            w.Write(payload);
    }

    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Dib(BitmapSource image)
    {
        var size = image.PixelWidth;
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        bgra.CopyPixels(pixels, size * 4, 0);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);
        w.Write(size);
        w.Write(size * 2); // XOR + AND 两部分
        w.Write((short)1);
        w.Write((short)32);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        for (var y = size - 1; y >= 0; y--) // BMP 自下而上
            w.Write(pixels, y * size * 4, size * 4);
        var maskStride = (size + 31) / 32 * 4;
        w.Write(new byte[maskStride * size]); // 全 0：透明度由 alpha 通道决定
        return ms.ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "ThreeBrookTrans.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
