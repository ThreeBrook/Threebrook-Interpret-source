using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 识别出的一个单词（中日韩文字一字一个），Box 是原图上的像素坐标。
/// SpaceBefore：引擎明确给出前面有没有空格（PaddleOCR）；null 时按间距推断（Windows OCR）。
/// </summary>
public sealed record OcrWord(string Text, Rect Box, bool? SpaceBefore = null);

/// <summary>识别出的一行，单词按阅读顺序排列。</summary>
public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public Rect Bounds => Words.Aggregate(Rect.Empty, (bounds, word) => Rect.Union(bounds, word.Box));
}

/// <summary>
/// 文字识别入口：设置里选"自动"时装了高精度组件（PaddleOCR）就用它，否则用 Windows 自带 OCR。
/// </summary>
public static class OcrService
{
    /// <summary>两个单词间距小于行高的这个比例时视为同一个单词。</summary>
    private const double WordGapRatio = 0.2;

    /// <summary>这次识别实际用哪个引擎：auto / windows / paddle。</summary>
    public static bool UsesPaddle => App.Settings.Screenshot.OcrEngine switch
    {
        "windows" => false,
        _ => OcrComponent.FindDirectory() != null, // 选了高精度但组件被删了，也退回 Windows OCR
    };

    public static async Task<string> RecognizeAsync(BitmapSource image, CancellationToken ct = default) =>
        ToText(await RecognizeLinesAsync(image, ct));

    /// <summary>识别出各行和每个单词在原图上的位置（钉图上选取文字用）。</summary>
    public static Task<List<OcrLine>> RecognizeLinesAsync(BitmapSource image, CancellationToken ct = default) =>
        UsesPaddle
            ? PaddleOcr.RecognizeLinesAsync(image, ct)
            : WindowsOcr.RecognizeLinesAsync(image, App.Settings.Screenshot.OcrLanguage, ct);

    /// <summary>按行拼接，行间换行。</summary>
    public static string ToText(IEnumerable<OcrLine> lines) => string.Join('\n', lines.Select(line => JoinWords(line.Words)));

    /// <summary>
    /// 拼接同一行的单词：引擎给出了空格就照用；否则中日韩文字之间不加空格，其他单词按间距决定
    /// （Windows OCR 有时把一个英文单词切成几段，等宽字体常见）。
    /// </summary>
    public static string JoinWords(IReadOnlyList<OcrWord> words)
    {
        var lineHeight = words.Count > 0 ? words.Max(w => w.Box.Height) : 0;
        var text = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i].Text;
            if (i > 0 && w.Length > 0 && text.Length > 0)
            {
                if (words[i].SpaceBefore is { } space)
                {
                    if (space)
                        text.Append(' ');
                }
                else if (!TextUtil.IsCjk(text[^1]) && !TextUtil.IsCjk(w[0]) && words[i].Box.X - words[i - 1].Box.Right > lineHeight * WordGapRatio)
                {
                    text.Append(' ');
                }
            }
            text.Append(w);
        }
        return text.ToString();
    }
}

/// <summary>
/// 高精度识别组件（PaddleOCR 模型 + onnxruntime.dll，约 37 MB）的位置、安装和删除。
/// 组件不随安装包发布，用户从官网下载组件 zip 后在设置里"从文件安装"，解压到数据目录的 ocr 文件夹；
/// 程序旁边的 ocr 文件夹（绿色版手动放置）也认。程序本身不联网下载。
/// </summary>
public static class OcrComponent
{
    public const string FolderName = "ocr";
    public static readonly string[] RequiredFiles = ["onnxruntime.dll", "det.onnx", "rec.onnx", "ppocrv5_dict.txt"];

    /// <summary>组件里的版本说明文件，第一行是版本。</summary>
    public const string InfoFile = "component.txt";

    public static string InstallDirectory => Path.Combine(SettingsStore.DataDirectory, FolderName);

    /// <summary>onnxruntime.dll 已加载时不能覆盖或删除，先放到这里，下次启动时替换。</summary>
    private static string PendingDirectory => Path.Combine(SettingsStore.DataDirectory, FolderName + ".new");

    private static string PendingRemoveMarker => Path.Combine(SettingsStore.DataDirectory, FolderName + ".remove");

    /// <summary>开发用：环境变量 THREEBROOK_OCR_DIR 指向组件文件夹（如 tools\ocr\cache）。</summary>
    public static string? FindDirectory()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("THREEBROOK_OCR_DIR"),
            Path.Combine(AppContext.BaseDirectory, FolderName),
            InstallDirectory,
        };
        return candidates.FirstOrDefault(dir => !string.IsNullOrEmpty(dir) && RequiredFiles.All(f => File.Exists(Path.Combine(dir, f))));
    }

    public static string? Version(string directory)
    {
        try
        {
            return File.ReadLines(Path.Combine(directory, InfoFile)).FirstOrDefault()?.Trim();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>启动时处理上次没能完成的替换 / 删除（当时 onnxruntime.dll 正被占用）。</summary>
    public static void ApplyPendingChanges()
    {
        try
        {
            if (File.Exists(PendingRemoveMarker))
            {
                if (Directory.Exists(InstallDirectory))
                    Directory.Delete(InstallDirectory, recursive: true);
                File.Delete(PendingRemoveMarker);
            }
            if (Directory.Exists(PendingDirectory))
            {
                if (Directory.Exists(InstallDirectory))
                    Directory.Delete(InstallDirectory, recursive: true);
                Directory.Move(PendingDirectory, InstallDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("ocr component pending change failed: " + ex.Message, "settings.log");
        }
    }

    /// <summary>从组件 zip 安装；返回 true 表示已生效，false 表示要重启程序后生效。zip 不对时抛出 InvalidDataException。</summary>
    public static bool Install(string zipPath)
    {
        var staging = InstallDirectory + ".tmp";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                // 组件 zip 里的文件可能直接在根目录，也可能在一层文件夹里
                var dll = zip.Entries.FirstOrDefault(e => e.Name.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidDataException(Loc.T("Ocr.NotComponent"));
                var prefix = dll.FullName[..^dll.Name.Length];
                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0))
                {
                    var target = Path.GetFullPath(Path.Combine(staging, entry.FullName[prefix.Length..]));
                    if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(Loc.T("Ocr.NotComponent")); // 不解压到组件文件夹以外
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
            if (!RequiredFiles.All(f => File.Exists(Path.Combine(staging, f))))
                throw new InvalidDataException(Loc.T("Ocr.NotComponent"));

            PaddleOcr.Unload();
            if (Directory.Exists(PendingDirectory))
                Directory.Delete(PendingDirectory, recursive: true);
            File.Delete(PendingRemoveMarker);
            if (IsInUse())
            {
                Directory.Move(staging, PendingDirectory); // 旧组件的 dll 还在用，旧组件照常用到重启，重启后替换
                return false;
            }
            if (Directory.Exists(InstallDirectory))
                Directory.Delete(InstallDirectory, recursive: true);
            Directory.Move(staging, InstallDirectory);
            return true;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>删除数据目录里的组件；返回 false 表示要重启程序后才能删掉。</summary>
    public static bool Remove()
    {
        PaddleOcr.Unload();
        if (Directory.Exists(PendingDirectory))
            Directory.Delete(PendingDirectory, recursive: true);
        if (IsInUse())
        {
            File.WriteAllText(PendingRemoveMarker, "");
            return false;
        }
        if (Directory.Exists(InstallDirectory))
            Directory.Delete(InstallDirectory, recursive: true);
        return true;
    }

    /// <summary>
    /// onnxruntime.dll 加载后要到程序退出才释放。先试着独占打开它：被占用就整个推迟到重启，
    /// 免得删掉一半（模型删了、dll 删不掉）让组件坏掉。
    /// </summary>
    private static bool IsInUse()
    {
        var dll = Path.Combine(InstallDirectory, "onnxruntime.dll");
        if (!File.Exists(dll))
            return false;
        try
        {
            using var stream = new FileStream(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
