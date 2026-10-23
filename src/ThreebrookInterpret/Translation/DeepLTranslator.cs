using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Translation;

/// <summary>DeepL API，需要 Auth Key；免费版 Key 以 ":fx" 结尾，自动使用 api-free 地址。</summary>
public sealed class DeepLTranslator : ITranslator
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly EngineConfig _config;

    public DeepLTranslator(EngineConfig config) => _config = config;

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct) =>
        Http.WithTimeout(Timeout, ct, async token =>
        {
            var key = _config.ApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                throw new TranslationException("未配置 DeepL Auth Key");

            var host = key.EndsWith(":fx", StringComparison.Ordinal) ? "api-free.deepl.com" : "api.deepl.com";
            var body = new Dictionary<string, object> { ["text"] = new[] { text }, ["target_lang"] = MapTarget(to) };
            if (from != Languages.Auto)
                body["source_lang"] = from.ToUpperInvariant();

            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v2/translate")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);

            using var response = await Http.Client.SendAsync(request, token);
            var json = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"DeepL 返回 {(int)response.StatusCode}：{json}");

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
        });

    private static string MapTarget(string code) => code switch
    {
        "zh" => "ZH-HANS",
        "en" => "EN-US",
        _ => code.ToUpperInvariant(),
    };
}
