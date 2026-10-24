using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Translation;

/// <summary>
/// OpenAI 兼容的 /chat/completions 接口。
/// 可接在线大模型（智谱、硅基流动、DeepSeek 等），也可接本地部署（Ollama、LM Studio、vLLM、llama.cpp server）。
/// </summary>
public sealed class OpenAiCompatibleTranslator : ITranslator, IGlossaryPromptTranslator
{
    private const string DefaultPrompt =
        "You are a professional translator. Translate the user's text into {target}. " +
        "Output only the translation itself, without explanations, notes, quotes or the original text. " +
        "Preserve line breaks, formatting, numbers, code and proper nouns.";

    private static readonly Regex ThinkBlock = new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled);

    private readonly EngineConfig _config;

    public OpenAiCompatibleTranslator(EngineConfig config) => _config = config;

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct) =>
        TranslateAsync(text, from, to, Array.Empty<GlossaryTerm>(), ct);

    /// <summary>术语表命中的词写进系统提示词，模型按指定译法翻译（不用占位符，译文更自然）。</summary>
    public Task<string> TranslateAsync(string text, string from, string to, IReadOnlyList<GlossaryTerm> terms, CancellationToken ct) =>
        Http.WithTimeout(TimeSpan.FromSeconds(Math.Max(5, _config.TimeoutSeconds)), ct, async token =>
        {
            if (string.IsNullOrWhiteSpace(_config.BaseUrl) || string.IsNullOrWhiteSpace(_config.Model))
                throw new TranslationException(Services.Loc.T("Engine.LlmNoConfig"));

            var target = Languages.Get(to).EnglishName;
            var prompt = (string.IsNullOrWhiteSpace(_config.SystemPrompt) ? DefaultPrompt : _config.SystemPrompt)
                .Replace("{target}", target);
            if (terms.Count > 0)
                prompt += "\nAlways translate these terms exactly as given (source → translation):\n" +
                          string.Join("\n", terms.Select(t => $"- {t.Find} → {t.Replace}"));

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
                    throw new TranslationException(Services.Loc.F("Engine.Returned", _config.Name, (int)response.StatusCode, json));

                using var doc = JsonDocument.Parse(json);
                var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                return StripThinking(content);
            }
        });

    /// <summary>读取接口提供的模型列表（GET {BaseUrl}/models），设置界面用来选择模型名。</summary>
    public static Task<List<string>> ListModelsAsync(EngineConfig config, CancellationToken ct) =>
        Http.WithTimeout(TimeSpan.FromSeconds(10), ct, async token =>
        {
            if (string.IsNullOrWhiteSpace(config.BaseUrl))
                throw new TranslationException(Services.Loc.T("Engine.NoBaseUrl"));

            using var request = new HttpRequestMessage(HttpMethod.Get, config.BaseUrl.TrimEnd('/') + "/models");
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            using var response = await Http.Client.SendAsync(request, token);
            var json = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new TranslationException(Services.Loc.F("Engine.ModelsFailed", (int)response.StatusCode, json));

            using var doc = JsonDocument.Parse(json);
            var models = new List<string>();
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                if (item.GetProperty("id").GetString() is { Length: > 0 } id)
                    models.Add(id);
            return models;
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
