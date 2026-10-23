using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Translation;

/// <summary>百度翻译开放平台（通用文本翻译），需要 APP ID + 密钥。</summary>
public sealed class BaiduTranslator : ITranslator
{
    private const string Url = "https://fanyi-api.baidu.com/api/trans/vip/translate";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly EngineConfig _config;

    public BaiduTranslator(EngineConfig config) => _config = config;

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct) =>
        Http.WithTimeout(Timeout, ct, async token =>
        {
            if (string.IsNullOrWhiteSpace(_config.AppId) || string.IsNullOrWhiteSpace(_config.SecretKey))
                throw new TranslationException("未配置百度翻译 APP ID 或密钥");

            var salt = Random.Shared.Next(100000, 999999).ToString();
            var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(_config.AppId + text + salt + _config.SecretKey))).ToLowerInvariant();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = from == Languages.Auto ? "auto" : MapCode(from),
                ["to"] = MapCode(to),
                ["appid"] = _config.AppId!,
                ["salt"] = salt,
                ["sign"] = sign,
            });
            using var response = await Http.Client.PostAsync(Url, content, token);
            var body = await response.Content.ReadAsStringAsync(token);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_code", out var code))
                throw new TranslationException($"百度翻译错误 {code}：{root.GetProperty("error_msg").GetString()}");

            var lines = new List<string>();
            foreach (var item in root.GetProperty("trans_result").EnumerateArray())
                lines.Add(item.GetProperty("dst").GetString() ?? "");
            return string.Join("\n", lines);
        });

    private static string MapCode(string code) => code switch
    {
        "ja" => "jp",
        "ko" => "kor",
        "vi" => "vie",
        "ms" => "may",
        "tl" => "fil",
        "km" => "hkm",
        "lo" => "lao",
        "my" => "bur",
        _ => code,
    };
}
