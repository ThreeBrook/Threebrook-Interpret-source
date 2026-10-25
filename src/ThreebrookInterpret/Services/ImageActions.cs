using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ThreebrookInterpret.Services;

/// <summary>截图、钉图共用的复制 / 保存操作。</summary>
public static class ImageActions
{
    public static void Copy(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        // 部分程序（如浏览器、聊天软件）优先读取 PNG 格式
        data.SetData("PNG", new MemoryStream(Encode(image, ".png")));
        SetClipboard(data);
    }

    /// <summary>复制钉图上选中的文字。</summary>
    public static void CopyText(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        SetClipboard(data);
    }

    /// <summary>剪贴板可能正被别的程序占用，稍等重试几次。</summary>
    private static void SetClipboard(DataObject data)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (i < 5)
            {
                Thread.Sleep(30);
            }
        }
    }

    /// <summary>保存图片，返回保存路径；用户取消时返回 null。</summary>
    public static string? Save(BitmapSource image, Window? owner)
    {
        var settings = App.Settings.Screenshot;
        var extension = settings.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
        var directory = EnsureDirectory(settings.SaveDirectory);
        var fileName = UniqueFileName(directory, $"Threebrook_{DateTime.Now:yyyyMMdd_HHmmss}", extension);

        string path;
        if (settings.AskSavePath)
        {
            var dialog = new SaveFileDialog
            {
                InitialDirectory = directory,
                FileName = fileName,
                Filter = Loc.T("Image.Filter"),
                FilterIndex = extension == ".jpg" ? 2 : 1,
            };
            if (dialog.ShowDialog(owner) != true)
                return null;
            path = dialog.FileName;
        }
        else
        {
            path = Path.Combine(directory, fileName);
        }

        try
        {
            File.WriteAllBytes(path, Encode(image, Path.GetExtension(path)));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (owner != null)
                MessageBox.Show(owner, Loc.F("Image.SaveFailed", ex.Message), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show(Loc.F("Image.SaveFailed", ex.Message), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>设置的目录不可用（如拔掉的 U 盘、无权限）时改用默认目录 图片\三溪通译。</summary>
    private static string EnsureDirectory(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                Directory.CreateDirectory(configured);
                return configured;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // 落到默认目录
            }
        }
        Directory.CreateDirectory(ScreenshotSettings.DefaultSaveDirectory);
        return ScreenshotSettings.DefaultSaveDirectory;
    }

    /// <summary>同一秒内连续保存不会互相覆盖：已存在时加 _2、_3。</summary>
    private static string UniqueFileName(string directory, string baseName, string extension)
    {
        var name = baseName + extension;
        for (var i = 2; File.Exists(Path.Combine(directory, name)); i++)
            name = $"{baseName}_{i}{extension}";
        return name;
    }

    private static byte[] Encode(BitmapSource image, string extension)
    {
        BitmapEncoder encoder = extension.ToLowerInvariant() is ".jpg" or ".jpeg"
            ? new JpegBitmapEncoder { QualityLevel = 92 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
