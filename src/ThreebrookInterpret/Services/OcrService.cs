using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 识别出的一个单词（中日韩文字一字一个），Box 是原图上的像素坐标。
/// SpaceBefore：引擎明确给出前面有没有空格（PaddleOCR）；null 时按间距推断（Windows OCR）。
/// </summary>
public sealed record OcrWord(string Text, Rect Box, bool? SpaceBefore = null);

/// <summary>识别出的一行，单词按阅读顺序排列。</summary>
public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public Rect Bounds => Words.Aggregate(Rect.Empty, (bounds, word) => Rect.Union(bounds, word.Box));
}

/// <summary>
/// 文字识别入口：设置里选"自动"时装了高精度组件（PaddleOCR）就用它，否则用 Windows 自带 OCR。
/// </summary>
public static class OcrService
{
    /// <summary>两个单词间距小于行高的这个比例时视为同一个单词。</summary>
    private const double WordGapRatio = 0.2;

    /// <summary>同一行里间隔超过行高的这个倍数，当成表格的两列（正文的字距、词距远小于一个行高）。</summary>
    private const double ColumnGapRatio = 1.0;

    /// <summary>这次识别实际用哪个引擎：auto / windows / paddle。</summary>
    public static bool UsesPaddle => App.Settings.Screenshot.OcrEngine switch
    {
        "windows" => false,
        _ => OcrComponent.FindDirectory() != null, // 选了高精度但组件被删了，也退回 Windows OCR
    };

    /// <summary>识别成文字：按版面排好（见 ToText），"合并换行"打开时接上折行。</summary>
    public static async Task<string> RecognizeAsync(BitmapSource image, CancellationToken ct = default) =>
        ToText(await RecognizeLinesAsync(image, ct), App.Settings.MergeLineBreaks);

    /// <summary>识别出各行和每个单词在原图上的位置（钉图上选取文字用）。</summary>
    public static Task<List<OcrLine>> RecognizeLinesAsync(BitmapSource image, CancellationToken ct = default) =>
        UsesPaddle
            ? PaddleOcr.RecognizeLinesAsync(image, ct)
            : WindowsOcr.RecognizeLinesAsync(image, App.Settings.Screenshot.OcrLanguage, ct);

    /// <summary>按行拼接，行间换行。</summary>
    /// <summary>
    /// 按版面拼成文字（用识别结果里每行的位置，比只看标点的 TextUtil.MergeLines 可靠）：
    /// 同一排的几段（表格的几列）之间用 Tab，排与排之间换行，行距明显变大处空一行分段；
    /// mergeWrapped 时只接上真正的折行——上一行排到了右边、下一行从左边接着写、都不是表格行或列表项。
    /// </summary>
    public static string ToText(IEnumerable<OcrLine> lines, bool mergeWrapped = false)
    {
        var rows = GroupRows(lines);
        if (rows.Count == 0)
            return "";
        var height = rows.Select(r => r.Bounds.Height).OrderBy(h => h).ElementAt(rows.Count / 2);
        var left = rows.Min(r => r.Bounds.Left);
        var right = rows.Max(r => r.Bounds.Right);

        var text = new StringBuilder(rows[0].Text);
        for (var i = 1; i < rows.Count; i++)
        {
            Row prev = rows[i - 1], row = rows[i];
            var wrapped = mergeWrapped && prev.Cells == 1 && row.Cells == 1
                          && prev.Bounds.Right >= right - height * 2      // 上一行排满了
                          && row.Bounds.Left <= prev.Bounds.Left + height * 3 // 下一行没有明显缩进
                          && row.Bounds.Left >= left - height
                          && !TextUtil.IsListItem(row.Text);
            if (row.Bounds.Top - prev.Bounds.Bottom > height)
                text.Append("\n\n"); // 段落间距
            else if (wrapped)
                TextUtil.AppendWrapped(text, row.Text);
            else
                text.Append('\n');
            if (!wrapped)
                text.Append(row.Text);
        }
        return text.ToString();
    }

    private sealed record Row(Rect Bounds, int Cells, string Text);

    /// <summary>纵向重叠超过矮的那段一半的算同一排；排内从左到右，段与段之间用 Tab。</summary>
    private static List<Row> GroupRows(IEnumerable<OcrLine> lines)
    {
        var groups = new List<List<OcrLine>>();
        foreach (var line in lines.Where(l => l.Words.Count > 0).OrderBy(l => l.Bounds.Top + l.Bounds.Height / 2))
        {
            var b = line.Bounds;
            var row = groups.FirstOrDefault(g =>
            {
                var r = g.Aggregate(Rect.Empty, (bounds, l) => Rect.Union(bounds, l.Bounds));
                return Math.Min(r.Bottom, b.Bottom) - Math.Max(r.Top, b.Top) > Math.Min(r.Height, b.Height) * 0.5;
            });
            if (row != null)
                row.Add(line);
            else
                groups.Add([line]);
        }
        return groups
            .Select(g => g.OrderBy(l => l.Bounds.Left).ToList())
            .Select(g => new Row(g.Aggregate(Rect.Empty, (bounds, l) => Rect.Union(bounds, l.Bounds)),
                g.Count + g.Sum(l => CountTabs(JoinWords(l.Words))), string.Join('\t', g.Select(l => JoinWords(l.Words)))))
            .OrderBy(r => r.Bounds.Top)
            .ToList();
    }

    private static int CountTabs(string text) => text.Count(c => c == '\t');

    /// <summary>
    /// 拼接同一行的单词：间隔超过一个行高的（表格里挨得近的两列）用 Tab；引擎给出了空格就照用；
    /// 否则中日韩文字之间不加空格，其他单词按间距决定（Windows OCR 有时把一个英文单词切成几段，等宽字体常见）。
    /// </summary>
    public static string JoinWords(IReadOnlyList<OcrWord> words)
    {
        var lineHeight = words.Count > 0 ? words.Max(w => w.Box.Height) : 0;
        var text = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i].Text;
            if (i > 0 && w.Length > 0 && text.Length > 0)
            {
                if (words[i].Box.X - words[i - 1].Box.Right > lineHeight * ColumnGapRatio)
                    text.Append('\t');
                else if (words[i].SpaceBefore is { } space)
                {
                    if (space)
                        text.Append(' ');
                }
                else if (!TextUtil.IsCjk(text[^1]) && !TextUtil.IsCjk(w[0]) && words[i].Box.X - words[i - 1].Box.Right > lineHeight * WordGapRatio)
                {
                    text.Append(' ');
                }
            }
            text.Append(w);
        }
        return text.ToString();
    }
}


/// <summary>
/// 高精度识别组件（PaddleOCR 模型 + onnxruntime.dll，约 47 MB）：发布的安装包和绿色版都把它放在程序旁边的 ocr 文件夹。
/// </summary>
public static class OcrComponent
{
    public const string FolderName = "ocr";
    public static readonly string[] RequiredFiles = ["onnxruntime.dll", "det.onnx", "rec.onnx", "dict.txt"];

    /// <summary>组件里的版本说明文件，第一行是版本。</summary>
    public const string InfoFile = "component.txt";

    /// <summary>开发用：环境变量 THREEBROOK_OCR_DIR 指向组件文件夹（如 tools\ocr\cache），没有就找程序旁边的 ocr。</summary>
    public static string? FindDirectory()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("THREEBROOK_OCR_DIR"),
            Path.Combine(AppContext.BaseDirectory, FolderName),
        };
        return candidates.FirstOrDefault(dir => !string.IsNullOrEmpty(dir) && RequiredFiles.All(f => File.Exists(Path.Combine(dir, f))));
    }

    public static string? Version(string directory)
    {
        try
        {
            return File.ReadLines(Path.Combine(directory, InfoFile)).FirstOrDefault()?.Trim();
        }
        catch (IOException)
        {
            return null;
        }
    }
}
