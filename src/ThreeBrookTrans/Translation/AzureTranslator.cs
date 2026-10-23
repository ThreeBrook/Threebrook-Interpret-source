using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThreeBrookTrans.Services;

namespace ThreeBrookTrans.Translation;

/// <summary>微软 Azure AI 翻译（官方 API，F0 免费档），需要 Key，多服务资源还需要 Region。</summary>
public sealed class AzureTranslator : ITranslator
{
    private const string Url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly EngineConfig _config;

    public AzureTranslator(EngineConfig config) => _config = config;

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct) =>
        Http.WithTimeout(Timeout, ct, async token =>
        {
            if (string.IsNullOrWhiteSpace(_config.ApiKey))
                throw new TranslationException("未配置 Azure 翻译 Key");

            var url = Url + "&to=" + MapCode(to) + (from == Languages.Auto ? "" : "&from=" + MapCode(from));
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new[] { new { Text = text } }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Ocp-Apim-Subscription-Key", _config.ApiKey);
            if (!string.IsNullOrWhiteSpace(_config.Region))
                request.Headers.Add("Ocp-Apim-Subscription-Region", _config.Region);

            using var response = await Http.Client.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"Azure 翻译返回 {(int)response.StatusCode}：{body}");

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
        });

    public static string MapCode(string code) => code switch
    {
        "zh" => "zh-Hans",
        "tl" => "fil",
        _ => code,
    };
}
