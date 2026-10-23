using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ThreeBrookTrans.Services;

/// <summary>截图、钉图共用的复制 / 保存操作。</summary>
public static class ImageActions
{
    public static void Copy(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        // 部分程序（如浏览器、聊天软件）优先读取 PNG 格式
        data.SetData("PNG", new MemoryStream(Encode(image, ".png")));

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
        var directory = string.IsNullOrWhiteSpace(settings.SaveDirectory) ? ScreenshotSettings.DefaultSaveDirectory : settings.SaveDirectory;
        var extension = settings.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
        var fileName = $"ThreeBrook_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
        Directory.CreateDirectory(directory);

        string path;
        if (settings.AskSavePath)
        {
            var dialog = new SaveFileDialog
            {
                InitialDirectory = directory,
                FileName = fileName,
                Filter = "PNG 图片|*.png|JPG 图片|*.jpg",
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

        File.WriteAllBytes(path, Encode(image, Path.GetExtension(path)));
        return path;
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
