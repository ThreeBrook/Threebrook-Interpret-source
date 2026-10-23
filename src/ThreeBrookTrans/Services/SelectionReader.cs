using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace ThreeBrookTrans.Services;

/// <summary>
/// 获取前台程序中选中的文字：
/// 1. 先用 UI Automation 直接读取选区（不碰剪贴板，适用于记事本、Word、多数原生控件）；
/// 2. 读不到时模拟 Ctrl+C，读取剪贴板后恢复原内容（适用于浏览器、PDF 阅读器等）。
/// 必须在 UI 线程调用（剪贴板要求 STA）。
/// </summary>
public static class SelectionReader
{
    private static readonly TimeSpan UiaTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(500);

    public static async Task<string?> GetSelectedTextAsync()
    {
        var uia = Task.Run(TryUiAutomation);
        if (await Task.WhenAny(uia, Task.Delay(UiaTimeout)) == uia && !string.IsNullOrWhiteSpace(uia.Result))
            return uia.Result.Trim();

        return (await CopyViaClipboardAsync())?.Trim();
    }

    private static string? TryUiAutomation()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element == null || !element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                return null;

            var ranges = ((TextPattern)pattern).GetSelection();
            return string.Concat(ranges.Select(r => r.GetText(-1)));
        }
        catch (Exception)
        {
            // 目标程序不支持或已关闭，交给剪贴板方式
            return null;
        }
    }

    private static async Task<string?> CopyViaClipboardAsync()
    {
        // 临时复制和恢复都不应记入剪贴板历史
        ClipboardHistory.BeginSuppress();
        try
        {
            var backup = BackupClipboard();
            var sequence = GetClipboardSequenceNumber();

            await KeyboardInput.ReleaseModifiersAsync();
            KeyboardInput.SendCtrl(KeyboardInput.VK_C);

            var waited = TimeSpan.Zero;
            while (GetClipboardSequenceNumber() == sequence && waited < CopyTimeout)
            {
                await Task.Delay(20);
                waited += TimeSpan.FromMilliseconds(20);
            }
            if (GetClipboardSequenceNumber() == sequence)
                return null; // 没有选中内容，剪贴板未变化

            await Task.Delay(30); // 等源程序写完所有格式
            var text = await RetryClipboard(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
            await RetryClipboard(() =>
            {
                if (backup != null)
                    Clipboard.SetDataObject(backup, copy: true);
                else
                    Clipboard.Clear();
                return true;
            });
            return text;
        }
        finally
        {
            ClipboardHistory.EndSuppress();
        }
    }

    /// <summary>复制剪贴板里所有能读出的格式，用于之后恢复。</summary>
    private static DataObject? BackupClipboard()
    {
        try
        {
            var source = Clipboard.GetDataObject();
            if (source == null)
                return null;

            var copy = new DataObject();
            foreach (var format in source.GetFormats(autoConvert: false))
            {
                try
                {
                    var data = source.GetData(format, autoConvert: false);
                    if (data != null)
                        copy.SetData(format, data, autoConvert: false);
                }
                catch (Exception)
                {
                    // 个别延迟渲染的格式读不出来，跳过
                }
            }
            return copy.GetFormats().Length > 0 ? copy : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>剪贴板可能正被其他程序占用，重试几次。</summary>
    private static async Task<T?> RetryClipboard<T>(Func<T?> action)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return action();
            }
            catch (COMException) when (i < 5)
            {
                await Task.Delay(30);
            }
            catch (COMException)
            {
                return default;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
