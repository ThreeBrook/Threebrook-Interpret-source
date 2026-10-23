using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ThreeBrookTrans.Services;

public sealed class AppSettings
{
    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>外观：auto 跟随系统 / light 浅色 / dark 深色。</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>"auto" = 中文→英文，其他语言→中文；否则为固定目标语言代码。</summary>
    public string TargetLanguage { get; set; } = "auto";

    public List<EngineConfig> Engines { get; set; } = new();

    public ScreenshotSettings Screenshot { get; set; } = new();

    public HistorySettings History { get; set; } = new();

    /// <summary>划词和截图识别的文字自动合并句子中间的硬换行（PDF、OCR 常见）。</summary>
    public bool MergeLineBreaks { get; set; } = true;

    public static AppSettings CreateDefault() => new()
    {
        Engines =
        {
            new EngineConfig { Id = "bing", Type = "bing", Name = "必应翻译", Enabled = true },
            new EngineConfig { Id = "yandex", Type = "yandex", Name = "Yandex", Enabled = true },
            new EngineConfig
            {
                Id = "local-llm",
                Type = "openai",
                Name = "本地大模型",
                Enabled = false,
                BaseUrl = "http://localhost:11434/v1",
                ApiKey = "",
                Model = "qwen3:8b",
            },
        },
    };
}

public sealed class HotkeySettings
{
    public string InputTranslate { get; set; } = "Alt+Q";
    public string SelectionTranslate { get; set; } = "Alt+D";
    public string Screenshot { get; set; } = "Alt+S";
    public string ScreenshotTranslate { get; set; } = "Alt+W";
    public string History { get; set; } = "Alt+H";
}

public sealed class HistorySettings
{
    public bool Enabled { get; set; } = true;
    public bool RecordText { get; set; } = true;
    public bool RecordImages { get; set; } = true;
    public int MaxItems { get; set; } = 500;
    public int MaxDays { get; set; } = 30;

    /// <summary>在历史窗口里回车 / 双击时，复制后自动粘贴到之前的窗口。</summary>
    public bool PasteOnSelect { get; set; } = true;
}

public sealed class ScreenshotSettings
{
    public static string DefaultSaveDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ThreeBrookTrans");

    /// <summary>留空使用 图片\ThreeBrookTrans。</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>保存时是否弹出"另存为"对话框；false 则直接保存到 SaveDirectory。</summary>
    public bool AskSavePath { get; set; } = true;

    /// <summary>png / jpg</summary>
    public string ImageFormat { get; set; } = "png";

    /// <summary>OCR 语言："auto" 跟随系统，或如 "zh-Hans-CN"、"en-US"、"ja"（需安装对应 Windows 语言包）。</summary>
    public string OcrLanguage { get; set; } = "auto";
}

public sealed class EngineConfig
{
    public string Id { get; set; } = "";

    /// <summary>bing / yandex / openai / baidu / tencent / youdao / azure / deepl</summary>
    public string Type { get; set; } = "";

    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    // 官方 API：百度 / 有道用 AppId + SecretKey，腾讯用 AppId(SecretId) + SecretKey + Region，
    // Azure 用 ApiKey + Region，DeepL 用 ApiKey
    public string? AppId { get; set; }
    public string? SecretKey { get; set; }
    public string? Region { get; set; }

    // OpenAI 兼容接口
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }

    /// <summary>关闭 Qwen3 等模型的思考模式，并过滤 &lt;think&gt; 内容。</summary>
    public bool DisableThinking { get; set; } = true;

    public double Temperature { get; set; } = 0.2;
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>自定义系统提示词，{target} 会被替换为目标语言名称。留空使用默认提示词。</summary>
    public string? SystemPrompt { get; set; }
}

public static class SettingsStore
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThreeBrookTrans");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            var settings = AppSettings.CreateDefault();
            Save(settings);
            return settings;
        }

        var json = File.ReadAllText(SettingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault();
    }

    /// <summary>深拷贝，设置窗口在副本上编辑，点"保存"才生效。</summary>
    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
