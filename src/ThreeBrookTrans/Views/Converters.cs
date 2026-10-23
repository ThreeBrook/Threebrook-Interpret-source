using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ThreeBrookTrans.Services;

namespace ThreeBrookTrans.Views;

/// <summary>历史条目 → 缩略图（按需解码，列表虚拟化时不常驻内存）。</summary>
public sealed class ThumbnailConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is HistoryEntry { IsImage: true } entry ? ClipboardHistory.LoadImage(entry, decodeWidth: 400) : null;

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
            return "刚刚";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes} 分钟前";
        if (time.Date == DateTime.Today)
            return time.ToString("HH:mm");
        if (time.Date == DateTime.Today.AddDays(-1))
            return "昨天 " + time.ToString("HH:mm");
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
