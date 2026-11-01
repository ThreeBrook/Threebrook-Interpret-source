using System.Text;

namespace ThreebrookInterpret.Services;

public static class TextUtil
{
    /// <summary>
    /// 合并 PDF / OCR 文本里句子中间的硬换行：
    /// 上一行以句末标点结尾、下一行是列表项或遇到空行时保留换行；
    /// 英文行尾连字符断词时去掉连字符直接拼接；中日韩文字直接拼接，其他加空格。
    /// </summary>
    public static string MergeLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new StringBuilder();
        var paragraphBreak = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                paragraphBreak = result.Length > 0;
                continue;
            }
            if (result.Length == 0)
            {
                result.Append(line);
                continue;
            }

            if (paragraphBreak)
                result.Append("\n\n").Append(line);
            else if (IsSentenceEnd(result[^1]) || IsListItem(line))
                result.Append('\n').Append(line);
            else
                AppendWrapped(result, line);
            paragraphBreak = false;
        }
        return result.ToString();
    }

    /// <summary>接上折行：英文行尾连字符断词时去掉连字符；中日韩文字直接拼接，其他加空格。</summary>
    public static void AppendWrapped(StringBuilder text, string line)
    {
        if (text.Length > 0 && line.Length > 0)
        {
            var prev = text[^1];
            if (prev == '-' && text.Length > 1 && char.IsLetter(text[^2]) && char.IsLower(line[0]))
                text.Length--; // "trans-\nlation" → "translation"
            else if (!IsCjk(prev) && !IsCjk(line[0]) && !char.IsWhiteSpace(prev))
                text.Append(' ');
        }
        text.Append(line);
    }

    public static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or ':' or ';' or '。' or '！' or '？' or '：' or '；' or '…';

    /// <summary>以项目符号或"1." "2)" "3、"开头的列表项，前面不接到上一行。</summary>
    public static bool IsListItem(string line)
    {
        line = line.TrimStart();
        if (line.Length == 0)
            return false;
        if (line[0] is '•' or '·' or '-' or '*' or '●' or '■' or '◆')
            return true;
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i]))
            i++;
        return i > 0 && i < line.Length && line[i] is '.' or ')' or '、' or '．';
    }
}
