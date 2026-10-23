using System.Collections.Generic;
using System.Linq;

namespace ThreebrookInterpret.Translation;

public sealed record Language(string Code, string Name, string EnglishName);

/// <summary>内部统一使用的语言代码，各引擎自行映射到自己的代码。</summary>
public static class Languages
{
    public const string Auto = "auto";

    public static IReadOnlyList<Language> All { get; } = new[]
    {
        new Language("zh", "中文", "Simplified Chinese"),
        new Language("en", "英语", "English"),
        new Language("ja", "日语", "Japanese"),
        new Language("ko", "韩语", "Korean"),
        new Language("vi", "越南语", "Vietnamese"),
        new Language("th", "泰语", "Thai"),
        new Language("id", "印尼语", "Indonesian"),
        new Language("ms", "马来语", "Malay"),
        new Language("tl", "菲律宾语", "Filipino"),
        new Language("km", "高棉语", "Khmer"),
        new Language("lo", "老挝语", "Lao"),
        new Language("my", "缅甸语", "Burmese"),
    };

    public static Language Get(string code) => All.FirstOrDefault(l => l.Code == code) ?? All[0];

    /// <summary>目标语言为 auto 时：原文主要是中文则译成英文，否则译成中文。</summary>
    public static string ResolveTarget(string text, string target) =>
        target != Auto ? target : (IsMostlyChinese(text) ? "en" : "zh");

    private static bool IsMostlyChinese(string text)
    {
        int han = 0, letters = 0;
        foreach (var c in text)
        {
            if (c is >= '぀' and <= 'ヿ' || c is >= '가' and <= '힯')
                return false; // 含假名或谚文：日语/韩语
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿')
                han++;
            if (char.IsLetter(c))
                letters++;
        }
        return letters > 0 && han * 10 >= letters * 3;
    }
}
