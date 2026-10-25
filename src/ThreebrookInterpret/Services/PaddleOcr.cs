using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 高精度识别：PaddleOCR PP-OCRv5 mobile（检测 + 识别两个 ONNX 模型）跑在 ONNX Runtime CPU 上，本机完成。
/// 截图里的文字都是横排，所以检测结果按水平矩形处理（不做旋转框、方向分类），省掉 OpenCV。
/// 识别模型是 CTC 输出，每个字落在哪几个时间步就能换算出它在行里的横向位置，钉图上逐字选取靠这个。
/// 参数取 PaddleOCR / RapidOCR 的默认值。
/// </summary>
public static class PaddleOcr
{
    // 检测（DB）：短边放大到 736，长边不超过 2000，边长取 32 的倍数
    private const int DetMinSide = 736, DetMaxSide = 2000;
    private const float DetThreshold = 0.3f, DetBoxThreshold = 0.5f, UnclipRatio = 1.6f;
    private const int MinBoxSize = 3;
    private static readonly float[] DetMean = [0.485f, 0.456f, 0.406f], DetStd = [0.229f, 0.224f, 0.225f];

    // 识别：行图高 48，宽按比例（至少 320），按宽高比排序后每批 6 行
    private const int RecHeight = 48, RecMinWidth = 320, RecBatch = 6;
    private const float RecMinScore = 0.5f;

    /// <summary>用完 2 分钟后释放模型（约几十 MB 内存），下次识别再加载。</summary>
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(2);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Timer IdleTimer = new(_ => Unload());
    private static OnnxSession? _det, _rec;
    private static string[] _dictionary = [];
    private static string? _loadedFrom;

    /// <summary>上次识别的检测、识别耗时（识别对比 --ocr-compare 用）。</summary>
    internal static string LastTiming { get; private set; } = "";
    private static long RunMs;

    public static Task<List<OcrLine>> RecognizeLinesAsync(BitmapSource image, CancellationToken ct = default)
    {
        image = WindowsOcr.Frozen(image);
        return Task.Run(async () =>
        {
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var picture = new Picture(bgra.PixelWidth, bgra.PixelHeight, new byte[bgra.PixelWidth * bgra.PixelHeight * 4]);
            bgra.CopyPixels(picture.Pixels, picture.Width * 4, 0);

            await Gate.WaitAsync(ct);
            try
            {
                IdleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                EnsureLoaded();
                RunMs = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var boxes = Detect(picture);
                var detectMs = watch.ElapsedMilliseconds;
                ct.ThrowIfCancellationRequested();
                var lines = Recognize(picture, boxes, ct);
                LastTiming = $"detect {detectMs} ms, recognize {watch.ElapsedMilliseconds - detectMs} ms (model {RunMs} ms), {boxes.Count} boxes";
                return lines;
            }
            finally
            {
                IdleTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan);
                Gate.Release();
            }
        }, ct);
    }

    /// <summary>释放模型（删除、替换组件前也要先释放）。onnxruntime.dll 本身加载后不能卸载。</summary>
    public static void Unload()
    {
        Gate.Wait();
        try
        {
            _det?.Dispose();
            _rec?.Dispose();
            _det = _rec = null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed record Picture(int Width, int Height, byte[] Pixels);

    // —— 加载 ——

    private static void EnsureLoaded()
    {
        var directory = OcrComponent.FindDirectory() ?? throw new InvalidOperationException(Loc.T("Ocr.ComponentMissing"));
        if (_det != null && _loadedFrom == directory)
            return;
        _det?.Dispose();
        _rec?.Dispose();
        _det = _rec = null;

        // onnxruntime.dll 在组件文件夹里，进程内只加载一次（换了组件位置也沿用第一次加载的）
        OnnxSession.Initialize(Path.Combine(directory, "onnxruntime.dll"));
        _loadedFrom = directory;

        // 实测（2.1GHz 至强）：4 线程最快，再多反而慢；默认的内存池保留（关掉慢约 25%），模型闲置 2 分钟后连同内存池一起释放
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        _det = new OnnxSession(Path.Combine(directory, "det.onnx"), threads);
        _rec = new OnnxSession(Path.Combine(directory, "rec.onnx"), threads);
        _dictionary = File.ReadAllLines(Path.Combine(directory, "ppocrv5_dict.txt"));
    }

    // —— 检测 ——

    /// <summary>找出文字行的位置（原图像素坐标），按从上到下、从左到右排好。</summary>
    private static List<Int32Rect> Detect(Picture picture)
    {
        var scale = Math.Min(picture.Width, picture.Height) < DetMinSide ? (double)DetMinSide / Math.Min(picture.Width, picture.Height) : 1.0;
        if (Math.Max(picture.Width, picture.Height) * scale > DetMaxSide)
            scale = (double)DetMaxSide / Math.Max(picture.Width, picture.Height);
        var width = Math.Max(32, (int)Math.Round(picture.Width * scale / 32) * 32);
        var height = Math.Max(32, (int)Math.Round(picture.Height * scale / 32) * 32);

        var input = new float[3 * height * width];
        Sample(picture, new Int32Rect(0, 0, picture.Width, picture.Height), input, width, height, width, DetMean, DetStd);
        // 输出 [1, 1, height, width]：每个像素是文字的概率
        var map = _det!.Run(input, [1, 3, height, width], (data, _) => data.ToArray());

        var boxes = new List<Int32Rect>();
        var visited = new bool[map.Length];
        var stack = new Stack<int>();
        for (var start = 0; start < map.Length; start++)
        {
            if (visited[start] || map[start] <= DetThreshold)
                continue;
            // 四连通找出一块文字区域
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % width, y = i / width;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                if (x > 0) Visit(i - 1);
                if (x < width - 1) Visit(i + 1);
                if (y > 0) Visit(i - width);
                if (y < height - 1) Visit(i + width);
            }

            double w = right - left + 1, h = bottom - top + 1;
            if (Math.Min(w, h) < MinBoxSize || BoxScore(map, width, left, top, right, bottom) < DetBoxThreshold)
                continue;
            // DB 预测的是缩进过的文字核心，按 面积 × 比例 ÷ 周长 向外扩回文字实际范围
            var d = w * h * UnclipRatio / (2 * (w + h));
            if (Math.Min(w, h) + 2 * d < MinBoxSize + 2)
                continue;
            var x0 = Math.Clamp((int)Math.Floor((left - d) * picture.Width / width), 0, picture.Width - 1);
            var y0 = Math.Clamp((int)Math.Floor((top - d) * picture.Height / height), 0, picture.Height - 1);
            var x1 = Math.Clamp((int)Math.Ceiling((right + 1 + d) * picture.Width / width), x0 + 1, picture.Width);
            var y1 = Math.Clamp((int)Math.Ceiling((bottom + 1 + d) * picture.Height / height), y0 + 1, picture.Height);
            boxes.Add(new Int32Rect(x0, y0, x1 - x0, y1 - y0));
        }
        // 字间空白里偶尔检出几个像素的小点，识别模型会硬认成字（如 "(cd)"）：比一般行高的一半还矮、又不比行高宽的去掉
        if (boxes.Count > 0)
        {
            var typical = boxes.Select(b => b.Height).OrderBy(h => h).ElementAt(boxes.Count / 2);
            boxes.RemoveAll(b => b.Height < typical * 0.5 && b.Width < typical);
        }
        return SortReadingOrder(boxes);

        void Visit(int n)
        {
            if (!visited[n] && map[n] > DetThreshold)
            {
                visited[n] = true;
                stack.Push(n);
            }
        }
    }

    private static double BoxScore(float[] map, int width, int left, int top, int right, int bottom)
    {
        double sum = 0;
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
                sum += map[y * width + x];
        return sum / ((right - left + 1) * (bottom - top + 1));
    }

    /// <summary>纵向中心相差不到半行高（按高的那个框算，引号等小框也能归到所在行）的算同一排，排内从左到右。</summary>
    private static List<Int32Rect> SortReadingOrder(List<Int32Rect> boxes)
    {
        var result = new List<Int32Rect>();
        foreach (var box in boxes.OrderBy(b => b.Y + b.Height / 2.0))
        {
            var center = box.Y + box.Height / 2.0;
            var rowStart = result.FindLastIndex(b => Math.Abs(b.Y + b.Height / 2.0 - center) > Math.Max(b.Height, box.Height) / 2.0) + 1;
            var index = result.FindIndex(rowStart, b => b.X > box.X);
            result.Insert(index < 0 ? result.Count : index, box);
        }
        return result;
    }

    // —— 识别 ——

    private static List<OcrLine> Recognize(Picture picture, List<Int32Rect> boxes, CancellationToken ct)
    {
        var lines = new OcrLine?[boxes.Count];
        // 宽高比相近的放一批，补白最少
        var order = Enumerable.Range(0, boxes.Count).OrderBy(i => (double)boxes[i].Width / boxes[i].Height).ToList();
        for (var batchStart = 0; batchStart < order.Count; batchStart += RecBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = order.Skip(batchStart).Take(RecBatch).ToList();
            var maxRatio = Math.Max((double)RecMinWidth / RecHeight, batch.Max(i => (double)boxes[i].Width / boxes[i].Height));
            var inputWidth = (int)Math.Ceiling(RecHeight * maxRatio);
            var plane = 3 * RecHeight * inputWidth;
            var input = new float[batch.Count * plane]; // 补白处是 0（归一化后的灰）
            var resizedWidths = new int[batch.Count];
            for (var b = 0; b < batch.Count; b++)
            {
                var box = boxes[batch[b]];
                resizedWidths[b] = Math.Clamp((int)Math.Ceiling(RecHeight * (double)box.Width / box.Height), 1, inputWidth);
                Sample(picture, box, input.AsSpan(b * plane, plane), resizedWidths[b], RecHeight, inputWidth, [0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f]);
            }

            var runWatch = System.Diagnostics.Stopwatch.StartNew();
            // 输出 [batch, steps, classes]，已经过 softmax。字典有一万八千多类，直接读输出缓冲区，不复制
            var decoded = _rec!.Run(input, [batch.Count, 3, RecHeight, inputWidth], (probs, dims) =>
            {
                RunMs += runWatch.ElapsedMilliseconds;
                int steps = (int)dims[1], classes = (int)dims[2];
                var result = new OcrLine?[batch.Count];
                for (var b = 0; b < batch.Count; b++)
                    result[b] = Decode(probs.Slice(b * steps * classes, steps * classes), steps, classes, boxes[batch[b]],
                        (double)inputWidth / steps * boxes[batch[b]].Width / resizedWidths[b]);
                return result;
            });
            for (var b = 0; b < batch.Count; b++)
                lines[batch[b]] = decoded[b];
        }
        return MergeRows(lines.Where(l => l != null).ToList()!);
    }

    /// <summary>
    /// 检测有时把同一行切成几段（如加粗的开头、行内代码底色处）。同一排、间距不到 1.5 个行高的段接成一行；
    /// 间距更大的（分栏、表格）保持分开。
    /// </summary>
    private static List<OcrLine> MergeRows(List<OcrLine> lines)
    {
        var result = new List<OcrLine>();
        foreach (var line in lines)
        {
            if (result.Count > 0)
            {
                var previous = result[^1];
                Rect a = previous.Bounds, b = line.Bounds;
                // 引号、句点这类小框只占半行高：重叠按矮的算，间距按高的算
                var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                var height = Math.Max(a.Height, b.Height);
                var gap = b.Left - a.Right;
                if (overlap > Math.Min(a.Height, b.Height) * 0.5 && gap > -height * 0.5 && gap < height * 1.5)
                {
                    // 接缝处只在两边都不是中日韩文字、且确实有间隙时补空格
                    var first = line.Words[0];
                    var space = gap > height * 0.2 && !TextUtil.IsCjk(previous.Words[^1].Text[^1]) && !TextUtil.IsCjk(first.Text[0]);
                    result[^1] = new OcrLine([.. previous.Words, first with { SpaceBefore = space }, .. line.Words.Skip(1)]);
                    continue;
                }
            }
            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// CTC 贪心解码：每个时间步取概率最大的字，连续相同的合并、去掉空白（第 0 类）。
    /// 字的横向位置 = 它占的时间步 × 每步对应的原图宽度（stepWidth）。
    /// </summary>
    private static OcrLine? Decode(ReadOnlySpan<float> probs, int steps, int classes, Int32Rect box, double stepWidth)
    {
        var chars = new List<(string Text, double Center)>();
        double scoreSum = 0;
        int previous = 0, runStart = 0;
        for (var t = 0; t <= steps; t++)
        {
            var best = 0;
            if (t < steps)
            {
                var row = probs.Slice(t * classes, classes);
                for (var c = 1; c < classes; c++)
                    if (row[c] > row[best])
                        best = c;
            }
            if (t < steps && best == previous)
                continue;
            if (previous != 0)
            {
                // 一个字的时间步结束：字典第 k 类对应第 k-1 行，最后多出的一类是空格
                var text = previous - 1 < _dictionary.Length ? _dictionary[previous - 1] : " ";
                chars.Add((text, (runStart + t) / 2.0 * stepWidth));
                scoreSum += probs[runStart * classes + previous];
            }
            previous = best;
            runStart = t;
        }
        if (chars.Count == 0 || scoreSum / chars.Count < RecMinScore || chars.All(c => c.Text == " "))
            return null;
        return new OcrLine(ToWords(chars, box));
    }

    /// <summary>
    /// 字与字的分界取两个字中心的中点；中日韩文字一字一个单词，英文、数字连在一起的算一个单词，空格记在下一个单词上。
    /// </summary>
    private static List<OcrWord> ToWords(List<(string Text, double Center)> chars, Int32Rect box)
    {
        var edges = new double[chars.Count + 1];
        for (var i = 1; i < chars.Count; i++)
            edges[i] = (chars[i - 1].Center + chars[i].Center) / 2;
        var firstHalf = chars.Count > 1 ? (chars[1].Center - chars[0].Center) / 2 : box.Height / 2.0;
        var lastHalf = chars.Count > 1 ? (chars[^1].Center - chars[^2].Center) / 2 : box.Height / 2.0;
        edges[0] = Math.Max(0, chars[0].Center - firstHalf);
        edges[^1] = Math.Min(box.Width, chars[^1].Center + lastHalf);

        var words = new List<OcrWord>();
        var space = false;
        int wordStart = -1;
        void Flush(int end)
        {
            if (wordStart < 0)
                return;
            var text = string.Concat(chars.Skip(wordStart).Take(end - wordStart).Select(c => c.Text));
            words.Add(new OcrWord(text, new Rect(box.X + edges[wordStart], box.Y, edges[end] - edges[wordStart], box.Height), words.Count > 0 ? space : null));
            space = false;
            wordStart = -1;
        }
        for (var i = 0; i < chars.Count; i++)
        {
            var text = chars[i].Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                Flush(i);
                space = words.Count > 0;
            }
            else if (TextUtil.IsCjk(text[0]))
            {
                Flush(i);
                wordStart = i;
                Flush(i + 1);
            }
            else if (wordStart < 0)
            {
                wordStart = i;
            }
        }
        Flush(chars.Count);
        return words;
    }

    // —— 图像预处理 ——

    /// <summary>
    /// 把原图的 source 区域双线性缩放成 width × height，按 BGR 三个通道写进 CHW 张量（行宽 stride），
    /// 每个通道 (值/255 - mean) / std。和 PaddleOCR 一样按 OpenCV 读图的 BGR 顺序。
    /// </summary>
    private static void Sample(Picture picture, Int32Rect source, Span<float> target, int width, int height, int stride, float[] mean, float[] std)
    {
        var plane = height * stride;
        var sx = (double)source.Width / width;
        var sy = (double)source.Height / height;
        for (var y = 0; y < height; y++)
        {
            var fy = Math.Clamp(source.Y + (y + 0.5) * sy - 0.5, source.Y, source.Y + source.Height - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, source.Y + source.Height - 1);
            var wy = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp(source.X + (x + 0.5) * sx - 0.5, source.X, source.X + source.Width - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, source.X + source.Width - 1);
                var wx = fx - x0;
                for (var c = 0; c < 3; c++)
                {
                    double P(int px, int py) => picture.Pixels[(py * picture.Width + px) * 4 + c];
                    var value = (P(x0, y0) * (1 - wx) + P(x1, y0) * wx) * (1 - wy) + (P(x0, y1) * (1 - wx) + P(x1, y1) * wx) * wy;
                    target[c * plane + y * stride + x] = (float)((value / 255 - mean[c]) / std[c]);
                }
            }
        }
    }
}
