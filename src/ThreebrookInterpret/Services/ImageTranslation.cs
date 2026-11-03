using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ThreebrookInterpret.Translation;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 原位翻译的一块文字（一个段落、一个表格格子或一行标签）。
/// Lines 是各行在原图上的位置，Text 是接好折行的原文，Translation 是译文（没翻译或不用翻译时为 null）。
/// </summary>
public sealed class TextRegion(List<Rect> lines, string text)
{
    public List<Rect> Lines { get; } = lines;
    public string Text { get; } = text;
    public string? Translation { get; set; }

    public Rect Bounds => Lines.Aggregate(Rect.Empty, Rect.Union);

    /// <summary>各行高度的中位数，译文字号按它定。</summary>
    public double LineHeight => Lines.Select(l => l.Height).OrderBy(h => h).ElementAt(Lines.Count / 2);
}

/// <summary>
/// 原位翻译（钉图上直接显示译文）：识别结果分成段落 → 一次请求翻译多段 → 由 TranslationOverlay 盖住原文画上译文。
/// </summary>
public static class ImageTranslation
{
    /// <summary>同一行里间隔超过行高的这个倍数就切开（表格的列、分栏各成一块），和 OcrService 的列间距一致。</summary>
    private const double ColumnGapRatio = 1.0;

    /// <summary>上一行至少这么宽（行高的倍数）才可能是折行：短标签、菜单项一行一块，不接成一句。</summary>
    private const double WrappedLineMinWidth = 8;

    /// <summary>一次请求最多这么多字，太长的拆成几次（必应等网页接口对长度有限制）。</summary>
    private const int MaxBatchChars = 1500;

    /// <summary>把识别出的各行分成要分别翻译的块：大间隔处切开，再把折行接回段落。</summary>
    public static List<TextRegion> Regions(IEnumerable<OcrLine> lines)
    {
        var pieces = lines.SelectMany(SplitColumns).OrderBy(p => p.Box.Top).ToList();
        // 同一排还有别的段（表格的一行）：表格每格单独翻译，上下两格不接成一句
        var inRow = pieces.Select(p => pieces.Any(o => o != p && SameRow(o.Box, p.Box))).ToList();
        var regions = new List<(List<Rect> Lines, StringBuilder Text, bool Cell)>();
        for (var i = 0; i < pieces.Count; i++)
        {
            var (box, text) = pieces[i];
            var target = inRow[i] ? -1 : regions.FindIndex(r => !r.Cell && ContinuesParagraph(r.Lines[^1], box, text));
            if (target >= 0)
            {
                regions[target].Lines.Add(box);
                TextUtil.AppendWrapped(regions[target].Text, text);
            }
            else
                regions.Add(([box], new StringBuilder(text), inRow[i]));
        }
        return regions.Select(r => new TextRegion(r.Lines, r.Text.ToString())).ToList();
    }

    private static bool SameRow(Rect a, Rect b) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > Math.Min(a.Height, b.Height) * 0.5
        && (a.Right <= b.Left || b.Right <= a.Left);

    /// <summary>一行在大间隔处切成几段，每段是 (位置, 文字)。</summary>
    private static IEnumerable<(Rect Box, string Text)> SplitColumns(OcrLine line)
    {
        var words = line.Words;
        if (words.Count == 0)
            yield break;
        var height = words.Max(w => w.Box.Height);
        var start = 0;
        for (var i = 1; i <= words.Count; i++)
        {
            if (i < words.Count && words[i].Box.X - words[i - 1].Box.Right <= height * ColumnGapRatio)
                continue;
            var segment = words.Skip(start).Take(i - start).ToList();
            var text = OcrService.JoinWords(segment).Trim();
            if (text.Length > 0)
                yield return (segment.Aggregate(Rect.Empty, (bounds, w) => Rect.Union(bounds, w.Box)), text);
            start = i;
        }
    }

    /// <summary>
    /// 下一行是上一行的折行：行高相近、紧挨在下面、左边对齐，上一行足够长且不比下一行短（段落里只有最后一行短），
    /// 下一行也不是列表项。
    /// </summary>
    private static bool ContinuesParagraph(Rect prev, Rect next, string nextText)
    {
        var height = Math.Max(prev.Height, next.Height);
        var ratio = prev.Height / next.Height;
        var gap = next.Top - prev.Bottom;
        // 段落最后一行常只剩几个字和标点（"口。"），识别框比整行矮，高度放宽些
        var shortLast = next.Width < prev.Width * 0.3;
        return (shortLast ? ratio is > 0.6 and < 1.8 : ratio is > 0.75 and < 1.33)
               && gap > -height * 0.3 && gap < height * 0.8
               && Math.Abs(next.Left - prev.Left) < height * 1.5
               && prev.Width >= height * WrappedLineMinWidth
               && prev.Right >= next.Right - height * 2
               && !TextUtil.IsListItem(nextText);
    }

    /// <summary>
    /// 用第一个启用的引擎翻译各块，结果写进 Translation；返回引擎名。
    /// 多块用换行连起来一次翻译，返回的行数对不上（引擎合并或拆开了句子）时再逐块翻译。
    /// </summary>
    public static async Task<string> TranslateAsync(IReadOnlyList<TextRegion> regions, CancellationToken ct)
    {
        var engine = App.Settings.Engines.FirstOrDefault(e => e.Enabled)
                     ?? throw new TranslationException(Loc.T("Translate.NoEngines"));
        var translator = TranslatorFactory.Create(engine) is { } created
            ? Glossary.Wrap(created, App.Settings)
            : throw new TranslationException(Loc.F("Translate.UnknownEngine", engine.Type));

        // 数字、符号不用翻译，原样留在图上
        var todo = regions.Where(r => r.Text.Any(char.IsLetter)).ToList();
        var to = Languages.ResolveTarget(string.Join("\n", todo.Select(r => r.Text)), App.Settings.TargetLanguage);
        foreach (var batch in Batches(todo))
        {
            var result = await translator.TranslateAsync(string.Join("\n", batch.Select(r => r.Text)), Languages.Auto, to, ct);
            var parts = result.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == batch.Count)
            {
                for (var i = 0; i < batch.Count; i++)
                    batch[i].Translation = parts[i];
                continue;
            }
            foreach (var region in batch)
                region.Translation = (await translator.TranslateAsync(region.Text, Languages.Auto, to, ct)).Trim();
        }
        return engine.Name;
    }

    private static IEnumerable<List<TextRegion>> Batches(List<TextRegion> regions)
    {
        var batch = new List<TextRegion>();
        var length = 0;
        foreach (var region in regions)
        {
            if (batch.Count > 0 && length + region.Text.Length > MaxBatchChars)
            {
                yield return batch;
                batch = [];
                length = 0;
            }
            batch.Add(region);
            length += region.Text.Length + 1;
        }
        if (batch.Count > 0)
            yield return batch;
    }
}
