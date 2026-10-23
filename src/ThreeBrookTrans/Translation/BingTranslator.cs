using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThreeBrookTrans.Translation;

/// <summary>
/// Bing 翻译网页版接口（非官方，免 Key）。
/// 先打开 bing.com/translator 页面取 IG / IID / key / token（约 1 小时有效，Cookie 由 HttpClient 保存），
/// 再调用 ttranslatev3。网页版单次最多 1000 字，长文本按行切分。
/// </summary>
public sealed class BingTranslator : ITranslator
{
    private const string Host = "https://www.bing.com";
    private const int MaxChunk = 1000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly Regex IgRegex = new("IG:\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex IidRegex = new("data-iid=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ParamsRegex = new(@"params_AbusePreventionHelper\s*=\s*\[(\d+),""([^""]+)""", RegexOptions.Compiled);

    private static readonly SemaphoreSlim SessionLock = new(1, 1);
    private static Session? _session;

    private sealed record Session(string Ig, string Iid, string Key, string Token, DateTime ExpiresAt);

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct) =>
        Http.WithTimeout(Timeout, ct, async token =>
        {
            var parts = new List<string>();
            foreach (var chunk in Split(text))
                parts.Add(await TranslateChunkAsync(chunk, from, to, token));
            return string.Join("\n", parts);
        });

    private static async Task<string> TranslateChunkAsync(string text, string from, string to, CancellationToken ct)
    {
        var result = await TrySendAsync(await GetSessionAsync(forceRefresh: false, ct), text, from, to, ct);
        // 会话过期时 Bing 返回空内容或错误对象，刷新一次再试
        result ??= await TrySendAsync(await GetSessionAsync(forceRefresh: true, ct), text, from, to, ct);
        return result ?? throw new TranslationException("Bing 翻译未返回结果（接口可能已变更）");
    }

    private static async Task<string?> TrySendAsync(Session session, string text, string from, string to, CancellationToken ct)
    {
        var url = $"{Host}/ttranslatev3?isVertical=1&IG={session.Ig}&IID={session.Iid}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fromLang"] = from == Languages.Auto ? "auto-detect" : MapCode(from),
            ["to"] = MapCode(to),
            ["text"] = text,
            ["token"] = session.Token,
            ["key"] = session.Key,
        });
        using var response = await Http.Client.PostAsync(url, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
            return null;

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;
        return doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }

    private static async Task<Session> GetSessionAsync(bool forceRefresh, CancellationToken ct)
    {
        await SessionLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _session != null && DateTime.UtcNow < _session.ExpiresAt)
                return _session;

            var html = await Http.Client.GetStringAsync(Host + "/translator", ct);
            var ig = IgRegex.Match(html);
            var iid = IidRegex.Match(html);
            var param = ParamsRegex.Match(html);
            if (!ig.Success || !param.Success)
                throw new TranslationException("无法解析 Bing 翻译页面（接口可能已变更）");

            _session = new Session(
                ig.Groups[1].Value,
                iid.Success ? iid.Groups[1].Value : "translator.5023",
                param.Groups[1].Value,
                param.Groups[2].Value,
                DateTime.UtcNow + SessionLifetime);
            return _session;
        }
        finally
        {
            SessionLock.Release();
        }
    }

    /// <summary>按行合并成不超过 MaxChunk 的片段；单行超长时硬切。</summary>
    private static IEnumerable<string> Split(string text)
    {
        if (text.Length <= MaxChunk)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var rest = line;
            if (current.Length > 0 && current.Length + rest.Length + 1 > MaxChunk)
            {
                yield return current.ToString();
                current.Clear();
            }
            while (rest.Length > MaxChunk)
            {
                yield return rest[..MaxChunk];
                rest = rest[MaxChunk..];
            }

            if (current.Length > 0)
                current.Append('\n');
            current.Append(rest);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    private static string MapCode(string code) => code switch
    {
        "zh" => "zh-Hans",
        "tl" => "fil",
        _ => code,
    };
}
