using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ThreebrookInterpret.Translation;

/// <summary>
/// Yandex 翻译（非官方，免 Key）：模拟 Yandex 安卓客户端的请求，
/// 用随机 UUID 作为设备标识。Yandex 修改接口后可能失效。
/// </summary>
public sealed class YandexTranslator : ITranslator
{
    private static readonly string DeviceId = Guid.NewGuid().ToString("N");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct) =>
        Http.WithTimeout(Timeout, ct, async token =>
        {
            var url = $"https://translate.yandex.net/api/v1/tr.json/translate?id={DeviceId}-0-0&srv=android";
            // lang 只写目标语言时由 Yandex 自动检测源语言
            var lang = from == Languages.Auto ? to : $"{from}-{to}";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = text, ["lang"] = lang });
            using var response = await Http.Client.PostAsync(url, content, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"Yandex 返回 {(int)response.StatusCode}：{body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var code) && code.GetInt32() != 200)
                throw new TranslationException($"Yandex 错误 {code.GetInt32()}：{body}");

            var parts = new List<string>();
            foreach (var item in root.GetProperty("text").EnumerateArray())
                parts.Add(item.GetString() ?? "");
            return string.Join("\n", parts);
        });
}
