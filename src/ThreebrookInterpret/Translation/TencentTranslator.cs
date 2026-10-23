using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Translation;

/// <summary>
/// 腾讯云机器翻译 TextTranslate，需要 SecretId（填在 AppId）+ SecretKey，签名方式 TC3-HMAC-SHA256。
/// 不支持高棉语、老挝语、缅甸语、菲律宾语。
/// </summary>
public sealed class TencentTranslator : ITranslator
{
    private const string Host = "tmt.tencentcloudapi.com";
    private const string Service = "tmt";
    private const string ContentType = "application/json; charset=utf-8";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly EngineConfig _config;

    public TencentTranslator(EngineConfig config) => _config = config;

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct) =>
        Http.WithTimeout(Timeout, ct, async token =>
        {
            if (string.IsNullOrWhiteSpace(_config.AppId) || string.IsNullOrWhiteSpace(_config.SecretKey))
                throw new TranslationException("未配置腾讯云 SecretId 或 SecretKey");

            var payload = JsonSerializer.Serialize(new
            {
                SourceText = text,
                Source = from == Languages.Auto ? "auto" : from,
                Target = to,
                ProjectId = 0,
            });
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var canonicalRequest = $"POST\n/\n\ncontent-type:{ContentType}\nhost:{Host}\n\ncontent-type;host\n{Sha256Hex(payload)}";
            var scope = $"{date}/{Service}/tc3_request";
            var stringToSign = $"TC3-HMAC-SHA256\n{timestamp}\n{scope}\n{Sha256Hex(canonicalRequest)}";
            var secretDate = Hmac(Encoding.UTF8.GetBytes("TC3" + _config.SecretKey), date);
            var secretService = Hmac(secretDate, Service);
            var secretSigning = Hmac(secretService, "tc3_request");
            var signature = Convert.ToHexString(Hmac(secretSigning, stringToSign)).ToLowerInvariant();

            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization",
                $"TC3-HMAC-SHA256 Credential={_config.AppId}/{scope}, SignedHeaders=content-type;host, Signature={signature}");
            request.Headers.Add("X-TC-Action", "TextTranslate");
            request.Headers.Add("X-TC-Version", "2018-03-21");
            request.Headers.Add("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-TC-Region", string.IsNullOrWhiteSpace(_config.Region) ? "ap-guangzhou" : _config.Region);

            using var response = await Http.Client.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(body);
            var result = doc.RootElement.GetProperty("Response");
            if (result.TryGetProperty("Error", out var error))
                throw new TranslationException($"腾讯翻译错误 {error.GetProperty("Code").GetString()}：{error.GetProperty("Message").GetString()}");
            return result.GetProperty("TargetText").GetString() ?? "";
        });

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static byte[] Hmac(byte[] key, string message) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
}
