using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ThreebrookInterpret.Services;

public sealed class AppSettings
{
    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>外观：auto 跟随系统 / light 浅色 / dark 深色。</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>界面语言：auto（跟随系统）/ zh / en。</summary>
    public string Language { get; set; } = "auto";

    /// <summary>"auto" = 中文→英文，其他语言→中文；否则为固定目标语言代码。</summary>
    public string TargetLanguage { get; set; } = "auto";

    public List<EngineConfig> Engines { get; set; } = new();

    public ScreenshotSettings Screenshot { get; set; } = new();

    public HistorySettings History { get; set; } = new();

    /// <summary>术语表：固定专业词的译法。</summary>
    public bool GlossaryEnabled { get; set; } = true;

    public List<GlossaryEntry> Glossary { get; set; } = new();

    /// <summary>划词和截图识别的文字自动合并句子中间的硬换行（PDF、OCR 常见）。</summary>
    public bool MergeLineBreaks { get; set; } = true;

    public static AppSettings CreateDefault() => CreateDefault(Loc.Resolve("auto") == "en");

    /// <param name="english">默认引擎名用哪种语言（第一次运行时按系统语言）。</param>
    private static AppSettings CreateDefault(bool english) => new()
    {
        Engines =
        {
            new EngineConfig { Id = "bing", Type = "bing", Name = english ? "Bing" : "必应翻译", Enabled = true },
            new EngineConfig { Id = "yandex", Type = "yandex", Name = "Yandex", Enabled = true },
            new EngineConfig
            {
                Id = "local-llm",
                Type = "openai",
                Name = english ? "Local model" : "本地大模型",
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

/// <summary>
/// 一对术语，双向生效：原文里出现哪一边，就固定翻成另一边（如 "三溪" ⇄ "Threebrook"）。
/// </summary>
public sealed class GlossaryEntry
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class ScreenshotSettings
{
    public static string DefaultSaveDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppInfo.Name);

    /// <summary>留空使用 图片\三溪通译。</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>保存时是否弹出"另存为"对话框；false 则直接保存到 SaveDirectory。</summary>
    public bool AskSavePath { get; set; } = true;

    /// <summary>png / jpg</summary>
    public string ImageFormat { get; set; } = "png";

    /// <summary>识别引擎：auto（装了高精度识别组件就用 PaddleOCR，否则 Windows 自带）/ windows / paddle。</summary>
    public string OcrEngine { get; set; } = "auto";

    /// <summary>OCR 语言（只对 Windows 自带识别有效）："auto" 跟随系统，或如 "zh-Hans-CN"、"en-US"、"ja"（需安装对应 Windows 语言包）。</summary>
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
    /// <summary>
    /// 绿色版：程序旁边有 portable 文件时，所有数据放在程序旁边的 data 文件夹，不碰 %AppData%，拷走整个文件夹就能接着用。
    /// </summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"));

    /// <summary>安装版数据目录（卸载程序勾选"同时删除"时删的也是它）。</summary>
    public static string InstalledDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThreebrookInterpret");

    public static string DataDirectory { get; } =
        IsPortable ? Path.Combine(AppContext.BaseDirectory, "data") : InstalledDataDirectory;

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
        var loaded = Normalize(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault());
        foreach (var engine in loaded.Engines)
        {
            engine.ApiKey = SecretProtector.Unprotect(engine.ApiKey);
            engine.SecretKey = SecretProtector.Unprotect(engine.SecretKey);
        }
        return loaded;
    }

    /// <summary>
    /// 手改配置时可能写出 "Hotkeys": null 之类，补回默认值，避免启动时空引用导致快捷键全部失效。
    /// </summary>
    /// <summary>默认引擎名（没改过的）跟着界面语言换：必应翻译 ⇄ Bing、本地大模型 ⇄ Local model。</summary>
    public static void LocalizeDefaultEngineNames(AppSettings settings, bool english)
    {
        foreach (var engine in settings.Engines)
        {
            foreach (var (zh, en) in new[] { ("必应翻译", "Bing"), ("本地大模型", "Local model") })
            {
                if (engine.Name == (english ? zh : en))
                    engine.Name = english ? en : zh;
            }
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Hotkeys ??= new HotkeySettings();
        settings.Screenshot ??= new ScreenshotSettings();
        settings.History ??= new HistorySettings();
        settings.Engines ??= AppSettings.CreateDefault().Engines;
        settings.Engines.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Type));
        settings.Glossary ??= new List<GlossaryEntry>();
        settings.Glossary.RemoveAll(g => g == null || string.IsNullOrWhiteSpace(g.Source) || string.IsNullOrWhiteSpace(g.Target));
        settings.Theme ??= "auto";
        settings.Language ??= "auto";
        settings.TargetLanguage ??= "auto";
        var h = settings.Hotkeys;
        h.InputTranslate ??= "";
        h.SelectionTranslate ??= "";
        h.Screenshot ??= "";
        h.ScreenshotTranslate ??= "";
        h.History ??= "";
        settings.Screenshot.SaveDirectory ??= "";
        settings.Screenshot.ImageFormat ??= "png";
        settings.Screenshot.OcrLanguage ??= "auto";
        return settings;
    }

    /// <summary>
    /// 配置读取失败时，先把原文件改名备份（settings.broken-时间.json），
    /// 之后用默认配置运行、正常保存也不会让原来的 API Key 等内容丢失。返回备份路径。
    /// </summary>
    public static string? BackupBrokenFile()
    {
        if (!File.Exists(SettingsPath))
            return null;
        var backup = Path.Combine(DataDirectory, $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Copy(SettingsPath, backup, overwrite: true);
        return backup;
    }

    /// <summary>深拷贝，设置窗口在副本上编辑，点"保存"才生效。</summary>
    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

    /// <summary>
    /// 先写临时文件再替换，写到一半崩溃或断电也不会留下半截配置。
    /// API Key、密钥加密后写入（内存里仍是明文），见 SecretProtector。
    /// </summary>
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var onDisk = Clone(settings);
        foreach (var engine in onDisk.Engines)
        {
            engine.ApiKey = SecretProtector.Protect(engine.ApiKey);
            engine.SecretKey = SecretProtector.Protect(engine.SecretKey);
        }
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(onDisk, JsonOptions));
        File.Move(temp, SettingsPath, overwrite: true);
    }
}
