using System.Collections.Generic;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Translation;

/// <summary>引擎类型说明，设置界面据此显示需要填写的字段。</summary>
/// <summary>引擎类型；名称和说明按界面语言取（键见 Localization/Strings.*.xaml 的 S.EngineType.*）。</summary>
public sealed record EngineType(string Type, string Fields)
{
    public string DisplayName => Services.Loc.T($"EngineType.{Type}");
    public string Help => Services.Loc.T($"EngineType.{Type}.Help");
}

public static class TranslatorFactory
{
    public static IReadOnlyList<EngineType> Types { get; } = new[]
    {
        new EngineType("bing", ""),
        new EngineType("yandex", ""),
        new EngineType("openai", "BaseUrl,ApiKey,Model,Llm"),
        new EngineType("baidu", "AppId,SecretKey"),
        new EngineType("tencent", "AppId,SecretKey,Region"),
        new EngineType("youdao", "AppId,SecretKey"),
        new EngineType("azure", "ApiKey,Region"),
        new EngineType("deepl", "ApiKey"),
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
