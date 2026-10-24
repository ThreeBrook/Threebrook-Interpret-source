namespace ThreebrookInterpret;

/// <summary>
/// 产品名称（界面、托盘、对话框、关于页统一从这里取）。
/// 三溪产品命名：三溪 + 两字，英文 "Threebrook + 意译"（b 小写，与三溪寻影、三溪知库一致）。
/// 程序文件名、数据目录、注册表自启项等内部标识仍为 ThreebrookInterpret，改了会让已有配置、历史和自启失效。
/// </summary>
public static class AppInfo
{
    public const string ChineseName = "三溪通译";

    /// <summary>中文界面"三溪通译 Threebrook Interpret"，英文界面"Threebrook Interpret"（快捷方式说明、卸载信息）。</summary>
    public static string FullName => Services.Loc.IsEnglish ? EnglishName : $"{ChineseName} {EnglishName}";

    /// <summary>按界面语言显示的产品名。</summary>
    public static string Name => Services.Loc.IsEnglish ? EnglishName : ChineseName;
    public const string EnglishName = "Threebrook Interpret";
    public const string Brand = "三溪 Threebrook";
    public const string ProductUrl = "https://interpret.threebrook.com/";
    public const string CompanyUrl = "https://www.threebrook.com/";

    /// <summary>三段版本号，例如 0.7.0（取自 csproj 的 Version）。</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
