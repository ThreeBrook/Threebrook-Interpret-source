using System;
using System.Windows;
using Microsoft.Win32;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 浅色 / 深色主题（三溪外观规范 §6）：默认跟随系统，设置里可固定；切换立即生效，不重启。
/// 颜色字典是 App.xaml 合并字典的第一项，界面一律用 DynamicResource 引用颜色。
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; }

    /// <summary>主题变化后通知非 WPF 部分（托盘菜单）。</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_previewMode));
        };
        Apply();
    }

    private static string? _previewMode;

    /// <param name="mode">设置窗口预览用；为空时使用已保存的设置。</param>
    public static void Apply(string? mode = null)
    {
        _previewMode = mode;
        mode ??= App.Settings.Theme;
        var dark = mode == "dark" || mode != "light" && SystemUsesDarkTheme();
        if (Application.Current.Resources.MergedDictionaries.Count > 0 && dark == IsDark && _applied)
            return;

        var colors = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ThreebrookInterpret;component/Theme/Colors.{(dark ? "Dark" : "Light")}.xaml"),
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (dictionaries.Count > 0)
            dictionaries[0] = colors;
        else
            dictionaries.Add(colors);

        IsDark = dark;
        _applied = true;
        Changed?.Invoke();
    }

    private static bool _applied;

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}
