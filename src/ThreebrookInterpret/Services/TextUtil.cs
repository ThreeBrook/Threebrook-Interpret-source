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

            var prev = result[^1];
            if (paragraphBreak)
                result.Append("\n\n");
            else if (IsSentenceEnd(prev) || IsListItem(line))
                result.Append('\n');
            else if (prev == '-' && result.Length > 1 && char.IsLetter(result[^2]) && char.IsLower(line[0]))
                result.Length--; // "trans-\nlation" → "translation"
            else if (!IsCjk(prev) && !IsCjk(line[0]))
                result.Append(' ');

            result.Append(line);
            paragraphBreak = false;
        }
        return result.ToString();
    }

    public static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or ':' or ';' or '。' or '！' or '？' or '：' or '；' or '…';

    private static bool IsListItem(string line)
    {
        if (line[0] is '•' or '·' or '-' or '*' or '●' or '■' or '◆')
            return true;
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i]))
            i++;
        return i > 0 && i < line.Length && line[i] is '.' or ')' or '、' or '．';
    }
}
