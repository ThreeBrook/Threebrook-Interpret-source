using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThreeBrookTrans.Services;

namespace ThreeBrookTrans.Translation;

/// <summary>原文里命中的术语：Find 出现在原文中，译文里固定用 Replace。</summary>
public sealed record GlossaryTerm(string Find, string Replace);

/// <summary>能把术语写进提示词的引擎（大模型），不需要占位符。</summary>
public interface IGlossaryPromptTranslator
{
    Task<string> TranslateAsync(string text, string from, string to, IReadOnlyList<GlossaryTerm> terms, CancellationToken ct);
}

/// <summary>
/// 术语表：每对术语双向生效，原文里出现哪一边就固定翻成另一边。
/// 大模型把命中的术语写进提示词；机器翻译引擎先把术语换成占位符 TBX1、TBX2…（实测必应、Yandex 都原样保留），
/// 翻完再换回目标译法。
/// </summary>
public static class Glossary
{
    private static readonly Regex Placeholder = new(@"\s*TBX(\d+)\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>启用且有术语时给引擎套上术语处理。</summary>
    public static ITranslator Wrap(ITranslator translator, AppSettings settings) =>
        settings.GlossaryEnabled && settings.Glossary.Count > 0 ? new GlossaryTranslator(translator, settings.Glossary) : translator;

    /// <summary>找出原文里出现的术语（长的优先，避免"三溪知库"被"三溪"先匹配）。</summary>
    public static List<GlossaryTerm> Match(string text, IEnumerable<GlossaryEntry> entries)
    {
        var terms = new List<GlossaryTerm>();
        foreach (var entry in entries)
        {
            if (Contains(text, entry.Source))
                terms.Add(new GlossaryTerm(entry.Source.Trim(), entry.Target.Trim()));
            else if (Contains(text, entry.Target))
                terms.Add(new GlossaryTerm(entry.Target.Trim(), entry.Source.Trim()));
        }
        return terms.OrderByDescending(t => t.Find.Length).ToList();
    }

    private static bool Contains(string text, string term) =>
        !string.IsNullOrWhiteSpace(term) && TermRegex(term.Trim()).IsMatch(text);

    /// <summary>英文术语按整词匹配（"API" 不匹配 "rapid"），中文直接匹配；都不区分大小写。</summary>
    private static Regex TermRegex(string term)
    {
        var pattern = Regex.Escape(term);
        if (char.IsLetterOrDigit(term[0]) && term[0] < 128)
            pattern = @"\b" + pattern;
        if (char.IsLetterOrDigit(term[^1]) && term[^1] < 128)
            pattern += @"\b";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>一次性把所有命中的术语换成占位符（单次匹配，长的优先，互不干扰）。</summary>
    public static string Protect(string text, IReadOnlyList<GlossaryTerm> terms)
    {
        var pattern = string.Join("|", terms.Select(t => "(" + TermRegex(t.Find) + ")"));
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.Replace(text, match =>
        {
            for (var i = 0; i < terms.Count; i++)
            {
                if (!match.Groups[i + 1].Success)
                    continue;
                // 后面紧跟数字时加空格，否则 "三溪2026" 会变成 TBX12026、被读成第 12026 个术语
                var end = match.Index + match.Length;
                var digitFollows = end < text.Length && char.IsDigit(text[end]);
                return $"TBX{i + 1}" + (digitFollows ? " " : "");
            }
            return match.Value;
        });
    }

    /// <summary>占位符换回目标译法；挨着中日韩文字时去掉引擎加的空格。</summary>
    public static string Restore(string translated, IReadOnlyList<GlossaryTerm> terms)
    {
        var result = Placeholder.Replace(translated, match =>
        {
            var index = int.Parse(match.Groups[1].Value) - 1;
            if (index < 0 || index >= terms.Count)
                return match.Value;
            var replacement = terms[index].Replace;
            var before = match.Index > 0 ? translated[match.Index - 1] : '\0';
            var after = match.Index + match.Length < translated.Length ? translated[match.Index + match.Length] : '\0';
            var leading = match.Value.Length > 0 && char.IsWhiteSpace(match.Value[0]) && NeedsSpace(before, replacement[0]) ? " " : "";
            var trailing = match.Value.Length > 0 && char.IsWhiteSpace(match.Value[^1]) && NeedsSpace(replacement[^1], after) ? " " : "";
            return leading + replacement + trailing;
        });
        return result.Trim();
    }

    /// <summary>两侧都不是中日韩文字（也不是开头 / 结尾）时才保留空格。</summary>
    private static bool NeedsSpace(char left, char right) =>
        left != '\0' && right != '\0' && !TextUtil.IsCjk(left) && !TextUtil.IsCjk(right);

    private sealed class GlossaryTranslator : ITranslator
    {
        private readonly ITranslator _inner;
        private readonly IReadOnlyList<GlossaryEntry> _entries;

        public GlossaryTranslator(ITranslator inner, IReadOnlyList<GlossaryEntry> entries)
        {
            _inner = inner;
            _entries = entries;
        }

        public async Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct)
        {
            var terms = Match(text, _entries);
            if (terms.Count == 0)
                return await _inner.TranslateAsync(text, from, to, ct);
            if (_inner is IGlossaryPromptTranslator llm)
                return await llm.TranslateAsync(text, from, to, terms, ct);

            var translated = await _inner.TranslateAsync(Protect(text, terms).Trim(), from, to, ct);
            return Restore(translated, terms);
        }
    }
}
