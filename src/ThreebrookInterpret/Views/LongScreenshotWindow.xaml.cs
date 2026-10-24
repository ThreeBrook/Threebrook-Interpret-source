using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThreebrookInterpret.Services;
using Rectangle = System.Drawing.Rectangle;

namespace ThreebrookInterpret.Views;

/// <summary>
/// 长截图：截图遮罩收起后，在选区四周画一圈边框（鼠标可穿透，滚轮直接作用于下面的窗口），
/// 旁边放控制条。每 100 毫秒截取一次选区，交给 ScrollStitcher 拼接；可自动向下滚动，滚到底自动停。
/// 边框和控制条用 SetWindowDisplayAffinity 对截图隐藏，放在选区里也不会被截进长图。
/// </summary>
public partial class LongScreenshotWindow : Window
{
    private static readonly TimeSpan CaptureInterval = TimeSpan.FromMilliseconds(100);
    private const int AutoScrollEveryTicks = 3; // 自动滚动：每 300 毫秒滚一格
    private const int EndAfterUnchanged = 8;    // 自动滚动时连续这么多帧没变化，认为到底了

    private readonly Rectangle _region;
    private readonly ScrollStitcher _stitcher;
    private readonly DispatcherTimer _timer = new() { Interval = CaptureInterval };
    private readonly Window _frame;
    private bool _auto;
    private int _tick;
    private int _unchanged;
    private bool _done;

    /// <param name="region">屏幕物理像素区域。</param>
    public LongScreenshotWindow(Rectangle region)
    {
        InitializeComponent();
        _region = region;
        _stitcher = new ScrollStitcher(region.Width, region.Height);
        _frame = CreateFrame();
        _timer.Tick += (_, _) => CaptureFrame();
        SizeText.Text = $"{region.Width} × {region.Height}";
        SourceInitialized += (_, _) =>
        {
            ExcludeFromCapture(this);
            HwndSource.FromHwnd(Handle)!.AddHook(WndProc);
            RegisterHotKey(Handle, EscHotkeyId, MOD_NOREPEAT, VK_ESCAPE); // 用户在别的窗口里滚动时也能按 Esc 取消
        };
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    public async void Start()
    {
        _frame.Show();
        Show();
        PlaceControls();
        // 等截图遮罩完全消失再开始截，免得第一帧截到遮罩残影
        await Task.Delay(250);
        CaptureFrame();
        _timer.Start();
    }

    // —— 截取与拼接 ——

    private void CaptureFrame()
    {
        if (_done)
            return;
        if (_auto && ++_tick % AutoScrollEveryTicks == 0)
            ScrollDown();

        var result = _stitcher.Add(ScreenCapture.CaptureRegionPixels(_region));
        _unchanged = result == StitchResult.Unchanged ? _unchanged + 1 : 0;
        SizeText.Text = $"{_stitcher.Width} × {_stitcher.TotalHeight}";

        switch (result)
        {
            case StitchResult.Appended:
                StatusText.Text = _auto ? "正在自动滚动并拼接…" : "正在拼接，继续向下滚动…";
                break;
            case StitchResult.NoOverlap:
                StatusText.Text = "滚动太快了，请往回滚一点";
                break;
            case StitchResult.Full:
                StatusText.Text = $"已达到最大长度 {ScrollStitcher.MaxHeight} 像素";
                SetAuto(false);
                _timer.Stop();
                break;
            case StitchResult.Unchanged when _auto && _unchanged >= EndAfterUnchanged:
                SetAuto(false);
                StatusText.Text = "已经滚到底了，可以复制或保存";
                break;
        }
    }

    private void Auto_Click(object sender, RoutedEventArgs e) => SetAuto(!_auto);

    private void SetAuto(bool on)
    {
        _auto = on;
        _unchanged = 0;
        AutoIcon.Text = (string)FindResource(on ? "I.Pause" : "I.Play");
        AutoText.Text = on ? "停止滚动" : "自动滚动";
        if (on)
        {
            // 滚轮消息发给鼠标下的窗口，先把鼠标移到选区中间
            SetCursorPos(_region.Left + _region.Width / 2, _region.Top + _region.Height / 2);
            StatusText.Text = "正在自动滚动并拼接…";
        }
    }

    private static void ScrollDown() => mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)-120), IntPtr.Zero);

    // —— 完成 ——

    private BitmapSource Finish()
    {
        _done = true;
        _timer.Stop();
        SetAuto(false);
        return _stitcher.ToBitmap();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        ImageActions.Copy(Finish());
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var image = Finish();
        Topmost = false;
        if (ImageActions.Save(image, this) != null)
        {
            Close();
            return;
        }
        // 取消保存：继续截
        Topmost = true;
        _done = false;
        _timer.Start();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        var image = Finish();
        // 长图通常比屏幕高，钉图时缩小到工作区高度的 85% 以内
        var workHeight = System.Windows.Forms.Screen.FromRectangle(_region).WorkingArea.Height;
        var zoom = Math.Min(1.0, workHeight * 0.85 / image.PixelHeight);
        new PinWindow(image, _region.Left, _region.Top, zoom).Show();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _done = true;
        _timer.Stop();
        UnregisterHotKey(Handle, EscHotkeyId);
        _frame.Close();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == EscHotkeyId)
        {
            handled = true;
            Close();
        }
        return IntPtr.Zero;
    }

    // —— 边框与控制条位置 ——

    /// <summary>选区四周的边框：鼠标穿透、不抢焦点、不进任务栏，对截图隐藏。</summary>
    private Window CreateFrame()
    {
        var frame = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new System.Windows.Controls.Border { BorderThickness = new Thickness(2) },
        };
        ((System.Windows.Controls.Border)frame.Content).SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "B.Brook");
        frame.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(frame).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ExcludeFromCapture(frame);
            SetWindowPos(hwnd, IntPtr.Zero, _region.Left - 3, _region.Top - 3, _region.Width + 6, _region.Height + 6, SWP_NOZORDER | SWP_NOACTIVATE);
        };
        return frame;
    }

    /// <summary>控制条放在选区右侧；放不下放左侧；都放不下放进选区右上角（反正对截图隐藏）。</summary>
    private void PlaceControls()
    {
        GetWindowRect(Handle, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var work = System.Windows.Forms.Screen.FromRectangle(_region).WorkingArea;

        int x;
        if (_region.Right + width <= work.Right)
            x = _region.Right;
        else if (_region.Left - width >= work.Left)
            x = _region.Left - width;
        else
            x = _region.Right - width;
        var y = Math.Clamp(_region.Top - 12, work.Top, Math.Max(work.Top, work.Bottom - height));
        SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Windows 10 2004 起支持；更早的系统上失败也没关系，控制条一般在选区外。</summary>
    private static void ExcludeFromCapture(Window window) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WDA_EXCLUDEFROMCAPTURE);

    /// <summary>界面预览用：显示一个示例进度。</summary>
    internal void ShowPreviewState(string status, int height)
    {
        StatusText.Text = status;
        SizeText.Text = $"{_region.Width} × {height}";
    }

    private const int EscHotkeyId = 1;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000, VK_ESCAPE = 0x1B;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
