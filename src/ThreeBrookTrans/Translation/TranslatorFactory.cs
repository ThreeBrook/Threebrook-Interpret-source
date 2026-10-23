using System.Collections.Generic;
using ThreeBrookTrans.Services;

namespace ThreeBrookTrans.Translation;

/// <summary>引擎类型说明，设置界面据此显示需要填写的字段。</summary>
public sealed record EngineType(string Type, string DisplayName, string Fields, string Help);

public static class TranslatorFactory
{
    public static IReadOnlyList<EngineType> Types { get; } = new[]
    {
        new EngineType("bing", "必应翻译（免 Key）", "", "Bing 网页版接口，非官方，免费；接口变更时可能失效。"),
        new EngineType("yandex", "Yandex（免 Key）", "", "Yandex 安卓客户端接口，非官方，免费；接口变更时可能失效。"),
        new EngineType("openai", "OpenAI 兼容接口 / 本地大模型", "BaseUrl,ApiKey,Model,Llm",
            "可接本地 Ollama（http://localhost:11434/v1）、LM Studio、vLLM，或在线的 DeepSeek、智谱、硅基流动等。"),
        new EngineType("baidu", "百度翻译", "AppId,SecretKey", "在 百度翻译开放平台 → 管理控制台 获取 APP ID 和密钥。"),
        new EngineType("tencent", "腾讯翻译", "AppId,SecretKey,Region", "在 腾讯云 → 访问管理 → API 密钥 获取 SecretId（填在 AppId）和 SecretKey。"),
        new EngineType("youdao", "有道翻译", "AppId,SecretKey", "在 有道智云 → 应用总览 获取应用 ID 和应用密钥。"),
        new EngineType("azure", "Azure 翻译", "ApiKey,Region", "在 Azure 门户的 Translator 资源 → 密钥和终结点 获取 Key 和位置/区域。"),
        new EngineType("deepl", "DeepL", "ApiKey", "在 DeepL 账户 → API Keys 获取，免费版 Key 以 :fx 结尾。"),
    };

    public static ITranslator? Create(EngineConfig config) => config.Type.ToLowerInvariant() switch
    {
        "bing" => new BingTranslator(),
        "yandex" => new YandexTranslator(),
        "openai" => new OpenAiCompatibleTranslator(config),
        "baidu" => new BaiduTranslator(config),
        "tencent" => new TencentTranslator(config),
        "youdao" => new YoudaoTranslator(config),
        "azure" => new AzureTranslator(config),
        "deepl" => new DeepLTranslator(config),
        _ => null,
    };
}
