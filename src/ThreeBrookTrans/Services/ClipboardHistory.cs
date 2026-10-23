using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ThreeBrookTrans.Services;

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>text / image</summary>
    public string Kind { get; set; } = "text";

    public DateTime Time { get; set; } = DateTime.Now;
    public string? Text { get; set; }

    /// <summary>图片文件名（位于 history\images 目录）。</summary>
    public string? ImageFile { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>图片像素的哈希，用于去重。</summary>
    public string? Hash { get; set; }

    [JsonIgnore] public bool IsImage => Kind == "image";
    [JsonIgnore] public string? ImagePath => ImageFile == null ? null : Path.Combine(ClipboardHistory.ImageDirectory, ImageFile);
}

/// <summary>
/// 剪贴板历史：监听剪贴板变化，记录文字和图片（图片保存为 PNG），只存在本机。
/// 跳过密码管理器等标记为"不要记录"的内容，以及本程序划词时的临时复制。
/// </summary>
public sealed class ClipboardHistory : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    /// <summary>单条文字最多保存的字数；更长的只截取开头，避免索引过大拖慢启动和保存。</summary>
    private const int MaxTextLength = 20_000;

    public static string Directory => Path.Combine(SettingsStore.DataDirectory, "history");
    public static string ImageDirectory => Path.Combine(Directory, "images");
    private static string IndexPath => Path.Combine(Directory, "index.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int _suppressDepth;
    private static uint _ignoreUpToSequence;

    private readonly HwndSource _source;
    private readonly DispatcherTimer _readTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ObservableCollection<HistoryEntry> Entries { get; } = new();

    public ClipboardHistory() : this(listen: true)
    {
    }

    /// <summary>界面预览用：不读本机历史、不监听剪贴板。</summary>
    internal static ClipboardHistory CreatePreview() => new(listen: false);

    private ClipboardHistory(bool listen)
    {
        if (!listen)
        {
            _source = null!;
            return;
        }
        Load();
        _readTimer.Tick += (_, _) =>
        {
            _readTimer.Stop();
            ReadClipboard();
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };

        _source = new HwndSource(new HwndSourceParameters("ThreeBrookTransClipboard") { ParentWindow = new IntPtr(-3) });
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
    }

    private static HistorySettings Settings => App.Settings.History;

    /// <summary>本程序临时改动剪贴板期间调用，结束后的这批变化都不记录。</summary>
    public static void BeginSuppress() => _suppressDepth++;

    public static void EndSuppress()
    {
        _suppressDepth = Math.Max(0, _suppressDepth - 1);
        _ignoreUpToSequence = GetClipboardSequenceNumber();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            // 很多程序一次复制会连续触发多次，稍等再读
            _readTimer.Stop();
            _readTimer.Start();
        }
        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        if (!Settings.Enabled)
            return;
        if (_suppressDepth > 0 || GetClipboardSequenceNumber() <= _ignoreUpToSequence)
            return;

        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null || ShouldSkip(data))
                return;

            // Excel、Word 复制时会同时放文字和位图，有文字时按文字记录
            var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (Settings.RecordText)
                    AddText(text.Length > MaxTextLength ? text[..MaxTextLength] : text);
            }
            else if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG"))
            {
                if (!Settings.RecordImages)
                    return;
                var image = ReadImage(data);
                if (image == null)
                    Log("image formats present but could not be read: " + string.Join(", ", data.GetFormats(false)));
                else
                    _ = AddImageAsync(image);
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
        {
            // 剪贴板被占用或格式异常，放弃这一次
            Log($"read failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Log(string message) => DiagnosticLog.Write(message, "history.log");

    /// <summary>遵守 Windows 约定的"不要记录"标记（密码管理器等会设置）。</summary>
    private static bool ShouldSkip(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore"))
            return true;
        // CanIncludeInClipboardHistory = 0 表示不允许进入历史
        if (data.GetData("CanIncludeInClipboardHistory") is MemoryStream stream && stream.Length >= 4)
        {
            var buffer = new byte[4];
            stream.Read(buffer, 0, 4);
            return BitConverter.ToInt32(buffer, 0) == 0;
        }
        return false;
    }

    private static BitmapSource? ReadImage(IDataObject data)
    {
        // 优先 PNG（保留清晰度），否则用位图格式
        if (data.GetData("PNG") is MemoryStream png)
        {
            try
            {
                var frame = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                // 解码得到的 BitmapFrameDecode 即使冻结，内部仍引用界面线程的解码器，
                // 后台线程编码保存时会报"另一个线程拥有该对象"，所以复制成独立位图
                var copy = new WriteableBitmap(frame);
                copy.Freeze();
                return copy;
            }
            catch (Exception ex)
            {
                Log($"PNG decode failed (stream length {png.Length}, position {png.Position}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (Clipboard.GetImage() is { } bitmap)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var copy = new WriteableBitmap(converted);
            copy.Freeze();
            return copy;
        }
        return null;
    }

    private void AddText(string text)
    {
        if (Entries.Count > 0 && !Entries[0].IsImage && Entries[0].Text == text)
        {
            Entries[0].Time = DateTime.Now;
            ScheduleSave();
            return;
        }

        // 同样的文字再次复制时移到最前，而不是重复记录
        var existing = Entries.FirstOrDefault(e => !e.IsImage && e.Text == text);
        if (existing != null)
            Entries.Remove(existing);

        Entries.Insert(0, existing ?? new HistoryEntry { Kind = "text", Text = text });
        Entries[0].Time = DateTime.Now;
        Trim();
        ScheduleSave();
    }

    private async Task AddImageAsync(BitmapSource image)
    {
        try
        {
            await AddImageCoreAsync(image);
        }
        catch (Exception ex)
        {
            // 调用方不等待这个任务，异常必须在这里记录，否则会被静默丢弃
            Log($"add image failed ({image.PixelWidth}x{image.PixelHeight} {image.Format}, {image.GetType().Name}, frozen={image.IsFrozen}): {ex}");
        }
    }

    private async Task AddImageCoreAsync(BitmapSource image)
    {
        var entry = new HistoryEntry { Kind = "image", Width = image.PixelWidth, Height = image.PixelHeight };
        entry.ImageFile = entry.Id + ".png";
        var path = entry.ImagePath!;

        var hash = await Task.Run(() =>
        {
            var stride = image.PixelWidth * ((image.Format.BitsPerPixel + 7) / 8);
            var pixels = new byte[stride * image.PixelHeight];
            image.CopyPixels(pixels, stride, 0);
            return Convert.ToHexString(SHA256.HashData(pixels));
        });

        var existing = Entries.FirstOrDefault(e => e.IsImage && e.Hash == hash);
        if (existing != null)
        {
            Entries.Remove(existing);
            existing.Time = DateTime.Now;
            Entries.Insert(0, existing);
            ScheduleSave();
            return;
        }

        await Task.Run(() =>
        {
            System.IO.Directory.CreateDirectory(ImageDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(path);
            encoder.Save(file);
        });

        entry.Hash = hash;
        Entries.Insert(0, entry);
        Trim();
        ScheduleSave();
    }

    /// <summary>把历史条目放回剪贴板，并移到最前（不产生重复记录）。</summary>
    public void CopyToClipboard(HistoryEntry entry)
    {
        BeginSuppress();
        try
        {
            if (entry.IsImage)
            {
                var image = LoadImage(entry);
                if (image == null)
                    return;
                ImageActions.Copy(image);
            }
            else
            {
                Clipboard.SetDataObject(entry.Text ?? "", copy: true);
            }
        }
        finally
        {
            EndSuppress();
        }

        Entries.Remove(entry);
        entry.Time = DateTime.Now;
        Entries.Insert(0, entry);
        ScheduleSave();
    }

    public static BitmapSource? LoadImage(HistoryEntry entry, int decodeWidth = 0)
    {
        var path = entry.ImagePath;
        if (path == null || !File.Exists(path))
            return null;

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.CacheOption = BitmapCacheOption.OnLoad; // 读完即释放文件，允许删除
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (decodeWidth > 0 && entry.Width > decodeWidth)
            image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Delete(HistoryEntry entry)
    {
        Entries.Remove(entry);
        DeleteFile(entry);
        ScheduleSave();
    }

    public void Clear()
    {
        foreach (var entry in Entries.ToList())
            DeleteFile(entry);
        Entries.Clear();
        Save();
    }

    /// <summary>按条数和天数清理旧记录。</summary>
    public void Trim()
    {
        var cutoff = DateTime.Now.AddDays(-Math.Max(1, Settings.MaxDays));
        var max = Math.Max(10, Settings.MaxItems);
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (i >= max || Entries[i].Time < cutoff)
            {
                DeleteFile(Entries[i]);
                Entries.RemoveAt(i);
            }
        }
    }

    private static void DeleteFile(HistoryEntry entry)
    {
        try
        {
            if (entry.ImagePath is { } path && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件被占用或无权删除，留到下次清理
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(IndexPath))
                return;
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(IndexPath), JsonOptions) ?? new();
            foreach (var entry in entries.Where(e => !e.IsImage || File.Exists(e.ImagePath)))
                Entries.Add(entry);
            Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 索引损坏或无权读取时从空历史开始，不影响主功能
            Log("load failed: " + ex.Message);
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private Task _saving = Task.CompletedTask;

    /// <summary>界面线程只取快照，序列化和写盘放到后台依次进行（条目多、文字长时不卡界面）。</summary>
    private void Save()
    {
        var snapshot = Entries.ToList();
        _saving = _saving.ContinueWith(_ => WriteIndex(snapshot), TaskScheduler.Default);
    }

    private static void WriteIndex(List<HistoryEntry> snapshot)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = IndexPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("save failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_source == null)
            return;
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        if (_saveTimer.IsEnabled)
            Save();
        _saving.Wait(TimeSpan.FromSeconds(3)); // 退出前等最后一次保存写完
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
