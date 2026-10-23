using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThreeBrookTrans.Services;

namespace ThreeBrookTrans.Translation;

/// <summary>
/// OpenAI 兼容的 /chat/completions 接口。
/// 可接在线大模型（智谱、硅基流动、DeepSeek 等），也可接本地部署（Ollama、LM Studio、vLLM、llama.cpp server）。
/// </summary>
public sealed class OpenAiCompatibleTranslator : ITranslator
{
    private const string DefaultPrompt =
        "You are a professional translator. Translate the user's text into {target}. " +
        "Output only the translation itself, without explanations, notes, quotes or the original text. " +
        "Preserve line breaks, formatting, numbers, code and proper nouns.";

    private static readonly Regex ThinkBlock = new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled);

    private readonly EngineConfig _config;

    public OpenAiCompatibleTranslator(EngineConfig config) => _config = config;

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct) =>
        Http.WithTimeout(TimeSpan.FromSeconds(Math.Max(5, _config.TimeoutSeconds)), ct, async token =>
        {
            if (string.IsNullOrWhiteSpace(_config.BaseUrl) || string.IsNullOrWhiteSpace(_config.Model))
                throw new TranslationException("未配置 BaseUrl 或 Model");

            var target = Languages.Get(to).EnglishName;
            var prompt = (string.IsNullOrWhiteSpace(_config.SystemPrompt) ? DefaultPrompt : _config.SystemPrompt)
                .Replace("{target}", target);

            var body = new Dictionary<string, object>
            {
                ["model"] = _config.Model!,
                ["temperature"] = _config.Temperature,
                ["stream"] = false,
                ["messages"] = new object[]
                {
                    new { role = "system", content = prompt },
                    new { role = "user", content = text },
                },
            };
            if (_config.DisableThinking)
            {
                body["chat_template_kwargs"] = new { enable_thinking = false }; // vLLM / SGLang / llama.cpp
                body["think"] = false; // Ollama
            }

            var request = new HttpRequestMessage(HttpMethod.Post, _config.BaseUrl!.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(_config.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            using (request)
            using (var response = await Http.Client.SendAsync(request, token))
            {
                var json = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                    throw new TranslationException($"{_config.Name} 返回 {(int)response.StatusCode}：{json}");

                using var doc = JsonDocument.Parse(json);
                var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                return StripThinking(content);
            }
        });

    private static string StripThinking(string content)
    {
        content = ThinkBlock.Replace(content, "");
        // 某些模板不输出开头的 <think>，只留下结尾标签
        var end = content.IndexOf("</think>", StringComparison.Ordinal);
        if (end >= 0)
            content = content[(end + "</think>".Length)..];
        return content.Trim();
    }
}
