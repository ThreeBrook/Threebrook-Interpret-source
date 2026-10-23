using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace ThreebrookInterpret.Services;

/// <summary>
/// 获取前台程序中选中的文字：
/// 1. 先用 UI Automation 直接读取选区（不碰剪贴板，适用于记事本、Word、多数原生控件）；
/// 2. 读不到时模拟 Ctrl+C，读取剪贴板后恢复原内容（适用于浏览器、PDF 阅读器等）。
/// 必须在 UI 线程调用（剪贴板要求 STA）。
/// </summary>
public static class SelectionReader
{
    /// <summary>含遍历网页文档的时间；超时后改用 Ctrl+C。</summary>
    private static readonly TimeSpan UiaTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>大页面的 Chromium / Electron 程序响应 Ctrl+C 可能较慢。</summary>
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(1200);

    public static async Task<string?> GetSelectedTextAsync()
    {
        KeyboardInput.MaskModifierTap(); // 必须在第一个 await 之前，趁快捷键的 Alt 还按着
        var window = DiagnosticLog.DescribeForegroundWindow();
        var sw = Stopwatch.StartNew();

        var uia = Task.Run(TryUiAutomation);
        var uiaFinished = await Task.WhenAny(uia, Task.Delay(UiaTimeout)) == uia;
        var uiaResult = uiaFinished ? uia.Result : (null, "timeout");
        if (!string.IsNullOrWhiteSpace(uiaResult.Text))
        {
            DiagnosticLog.Write($"[{window}] UIA ok {uiaResult.Text.Length} chars, {uiaResult.Note}, {sw.ElapsedMilliseconds}ms");
            return uiaResult.Text.Trim();
        }

        var (text, copyNote) = await CopyViaClipboardAsync();
        DiagnosticLog.Write($"[{window}] UIA {uiaResult.Note}; Ctrl+C {copyNote}; total {sw.ElapsedMilliseconds}ms");
        return text?.Trim();
    }

    private static (string? Text, string Note) TryUiAutomation()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            var focused = element?.Current.ControlType.ProgrammaticName ?? "nothing";
            var text = GetSelection(element);
            if (!string.IsNullOrWhiteSpace(text))
                return (text, $"selection on focused {focused}");

            // 聊天类程序（如 Claude）把键盘焦点固定在输入框上，
            // 用户在对话区选中的文字不在焦点控件里，而在窗口的网页文档里
            text = GetDocumentSelection(GetForegroundWindow());
            if (!string.IsNullOrWhiteSpace(text))
                return (text, $"selection in document (focused {focused} was empty)");

            return (null, $"empty selection (focused {focused}, documents empty)");
        }
        catch (Exception ex)
        {
            // 目标程序不支持或已关闭，交给剪贴板方式
            return (null, ex.GetType().Name);
        }
    }

    private static string? GetSelection(AutomationElement? element)
    {
        if (element == null || !element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            return null;
        return string.Concat(((TextPattern)pattern).GetSelection().Select(r => r.GetText(-1)));
    }

    private static string? GetDocumentSelection(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;
        var documents = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        foreach (AutomationElement document in documents)
        {
            var text = GetSelection(document);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return null;
    }

    private const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);

    /// <summary>超时后再观察这么久：响应很慢的程序迟到的复制也要恢复、且不记入历史。</summary>
    private static readonly TimeSpan LateCopyGrace = TimeSpan.FromMilliseconds(1500);

    /// <summary>剪贴板备份。Data 为空表示原来就是空的（恢复时清空）；备份失败时整个对象为 null。</summary>
    private sealed record ClipboardBackup(DataObject? Data);

    private static async Task<(string? Text, string Note)> CopyViaClipboardAsync()
    {
        // 临时复制和恢复都不应记入剪贴板历史
        ClipboardHistory.BeginSuppress();
        var handedOff = false;
        try
        {
            // 剪贴板被其他程序占着读不出来时不发 Ctrl+C，否则无法恢复，会把用户原来的内容弄丢
            var backup = await BackupClipboardAsync();
            if (backup == null)
                return (null, "clipboard busy, Ctrl+C skipped to keep its contents");
            var sequence = GetClipboardSequenceNumber();

            await KeyboardInput.ReleaseModifiersAsync();
            KeyboardInput.SendCtrl(KeyboardInput.VK_C);

            var sw = Stopwatch.StartNew();
            while (GetClipboardSequenceNumber() == sequence && sw.Elapsed < CopyTimeout)
                await Task.Delay(20);
            if (GetClipboardSequenceNumber() == sequence)
            {
                // 没有选中内容，或程序响应太慢：继续观察一会儿，迟到的复制由 WatchLateCopyAsync 恢复并结束屏蔽
                handedOff = true;
                _ = WatchLateCopyAsync(sequence, backup);
                return (null, $"clipboard unchanged after {sw.ElapsedMilliseconds}ms");
            }

            var changedAfter = sw.ElapsedMilliseconds;
            await Task.Delay(30); // 等源程序写完所有格式
            var text = await RetryClipboard(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
            var note = text == null ? $"clipboard changed after {changedAfter}ms but has no text" : $"ok {text.Length} chars after {changedAfter}ms";
            await RestoreAsync(backup);
            return (text, note);
        }
        finally
        {
            if (!handedOff)
                ClipboardHistory.EndSuppress();
        }
    }

    private static async Task WatchLateCopyAsync(uint sequence, ClipboardBackup backup)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < LateCopyGrace)
            {
                await Task.Delay(50);
                if (GetClipboardSequenceNumber() == sequence)
                    continue;
                await Task.Delay(30);
                await RestoreAsync(backup);
                DiagnosticLog.Write($"late copy after timeout restored ({sw.ElapsedMilliseconds}ms into grace period)");
                return;
            }
        }
        finally
        {
            ClipboardHistory.EndSuppress();
        }
    }

    private static Task RestoreAsync(ClipboardBackup backup) => RetryClipboard(() =>
    {
        if (backup.Data != null)
            Clipboard.SetDataObject(backup.Data, copy: true);
        else
            Clipboard.Clear();
        return true;
    });

    /// <summary>复制剪贴板里所有能读出的格式，用于之后恢复；剪贴板一直被占用时返回 null。</summary>
    private static async Task<ClipboardBackup?> BackupClipboardAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var source = Clipboard.GetDataObject();
                if (source == null)
                    return new ClipboardBackup(null);

                var formats = source.GetFormats(autoConvert: false);
                var copy = new DataObject();
                foreach (var format in formats)
                {
                    try
                    {
                        var data = source.GetData(format, autoConvert: false);
                        if (data != null)
                            copy.SetData(format, data, autoConvert: false);
                    }
                    catch (COMException ex) when (ex.HResult == CLIPBRD_E_CANT_OPEN)
                    {
                        // 列格式不需要打开剪贴板，读数据需要：被其他程序占着时在这里才失败，整体当作"忙"
                        throw;
                    }
                    catch (Exception)
                    {
                        // 个别延迟渲染的格式读不出来，跳过
                    }
                }
                // 剪贴板里有东西却一样都没读出来，同样当作"忙"，不能当成空剪贴板去清空
                if (formats.Length > 0 && copy.GetFormats().Length == 0)
                    throw new COMException("clipboard content unreadable", CLIPBRD_E_CANT_OPEN);
                return new ClipboardBackup(copy.GetFormats().Length > 0 ? copy : null);
            }
            catch (COMException)
            {
                await Task.Delay(30);
            }
        }
        return null;
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
