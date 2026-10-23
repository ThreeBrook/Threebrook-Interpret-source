using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Translation;

/// <summary>有道智云文本翻译，需要应用 ID（AppId）+ 应用密钥（SecretKey），签名 v3。</summary>
public sealed class YoudaoTranslator : ITranslator
{
    private const string Url = "https://openapi.youdao.com/api";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly EngineConfig _config;

    public YoudaoTranslator(EngineConfig config) => _config = config;

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct) =>
        Http.WithTimeout(Timeout, ct, async token =>
        {
            if (string.IsNullOrWhiteSpace(_config.AppId) || string.IsNullOrWhiteSpace(_config.SecretKey))
                throw new TranslationException("未配置有道应用 ID 或应用密钥");

            var salt = Guid.NewGuid().ToString("N");
            var curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var input = text.Length <= 20 ? text : text[..10] + text.Length + text[^10..];
            var sign = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_config.AppId + input + salt + curtime + _config.SecretKey))).ToLowerInvariant();

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = from == Languages.Auto ? "auto" : MapCode(from),
                ["to"] = MapCode(to),
                ["appKey"] = _config.AppId!,
                ["salt"] = salt,
                ["sign"] = sign,
                ["signType"] = "v3",
                ["curtime"] = curtime,
            });
            using var response = await Http.Client.PostAsync(Url, content, token);
            var body = await response.Content.ReadAsStringAsync(token);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.GetProperty("errorCode").GetString();
            if (code != "0")
                throw new TranslationException($"有道翻译错误码 {code}");

            var lines = new List<string>();
            foreach (var item in root.GetProperty("translation").EnumerateArray())
                lines.Add(item.GetString() ?? "");
            return string.Join("\n", lines);
        });

    private static string MapCode(string code) => code == "zh" ? "zh-CHS" : code;
}
