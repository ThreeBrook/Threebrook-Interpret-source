using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 界面语言（简体中文 / English）：文字在 Localization/Strings.zh.xaml、Strings.en.xaml，
/// 和颜色字典一样整份替换，XAML 里用 DynamicResource 引用的文字立即更新；代码里用 T / F 取。
/// 设置为 auto 时跟随 Windows 显示语言：中文系统用中文，其余用英文。
/// </summary>
public static class Loc
{
    public static bool IsEnglish { get; private set; }

    /// <summary>语言变化后通知非 WPF 部分（托盘菜单）和代码里设置的文字。</summary>
    public static event Action? Changed;

    public static string Resolve(string? mode) => mode switch
    {
        "zh" => "zh",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en",
    };

    public static void Apply(string? mode)
    {
        var lang = Resolve(mode);
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ThreebrookInterpret;component/Localization/Strings.{lang}.xaml"),
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Localization/Strings.") == true);
        var changed = current == null || IsEnglish != (lang == "en");
        if (current != null)
        {
            if (!changed)
                return;
            dictionaries[dictionaries.IndexOf(current)] = dictionary;
        }
        else
        {
            dictionaries.Add(dictionary);
        }
        IsEnglish = lang == "en";
        Changed?.Invoke();
    }

    /// <summary>取文字；键不存在时返回键名，方便发现漏掉的文字。</summary>
    public static string T(string key) => Application.Current?.TryFindResource("S." + key) as string ?? key;

    public static string F(string key, params object?[] args) => string.Format(T(key), args);
}
