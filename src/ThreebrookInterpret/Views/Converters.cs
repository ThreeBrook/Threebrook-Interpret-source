using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Views;

/// <summary>
/// 历史条目 → 缩略图。按需解码，并缓存最近用过的若干张：每次打开历史窗口都会刷新列表，
/// 不缓存的话可见的图片都要在界面线程上重新解码一遍。
/// </summary>
public sealed class ThumbnailConverter : IValueConverter
{
    private const int CacheSize = 60;
    private const int DecodeWidth = 360;

    private static readonly Dictionary<string, LinkedListNode<(string Id, BitmapSource Image)>> Cache = new();
    private static readonly LinkedList<(string Id, BitmapSource Image)> Recent = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not HistoryEntry { IsImage: true } entry)
            return null;

        if (Cache.TryGetValue(entry.Id, out var node))
        {
            Recent.Remove(node);
            Recent.AddFirst(node);
            return node.Value.Image;
        }

        var image = ClipboardHistory.LoadImage(entry, DecodeWidth);
        if (image == null)
            return null;
        Cache[entry.Id] = Recent.AddFirst((entry.Id, image));
        if (Recent.Count > CacheSize)
        {
            Cache.Remove(Recent.Last!.Value.Id);
            Recent.RemoveLast();
        }
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>时间 → "刚刚 / 5 分钟前 / 14:32 / 昨天 14:32 / 10-21 14:32"。</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime time)
            return "";
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1)
            return Loc.T("Time.JustNow");
        if (span.TotalMinutes < 60)
            return Loc.F("Time.MinutesAgo", (int)span.TotalMinutes);
        if (time.Date == DateTime.Today)
            return time.ToString("HH:mm");
        if (time.Date == DateTime.Today.AddDays(-1))
            return Loc.F("Time.Yesterday", time.ToString("HH:mm"));
        return time.ToString("MM-dd HH:mm");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
