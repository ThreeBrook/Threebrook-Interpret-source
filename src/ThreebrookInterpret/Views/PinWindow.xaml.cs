using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Views;

/// <summary>
/// 钉在桌面的截图：置顶、可拖动；滚轮缩放，Ctrl+滚轮调透明度，双击或 Esc 关闭。
/// 尺寸按物理像素设置，100% 时与原屏幕像素一一对应。
/// </summary>
public partial class PinWindow : Window
{
    private const double MinZoom = 0.1, MaxZoom = 5.0, ZoomStep = 1.1;
    private const double MinOpacity = 0.2;

    private readonly BitmapSource _image;
    private readonly DispatcherTimer _labelTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _x, _y;
    private double _zoom = 1.0;

    /// <param name="x">左上角屏幕物理像素坐标。</param>
    /// <param name="zoom">初始缩放（长截图比屏幕高时缩小显示）。</param>
    public PinWindow(BitmapSource image, int x, int y, double zoom = 1.0)
    {
        InitializeComponent();
        _image = image;
        _x = x;
        _y = y;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        PinImage.Source = image;
        _labelTimer.Tick += (_, _) =>
        {
            _labelTimer.Stop();
            ZoomLabel.Visibility = Visibility.Collapsed;
        };
        SourceInitialized += (_, _) =>
        {
            // 尺寸按物理像素管理，忽略 DPI 变化，否则在缩放比例不同的显示器上会被系统缩放、不再 1:1
            HwndSource.FromHwnd(Handle)!.AddHook(IgnoreDpiChange);
            ApplySize();
        };
        Loaded += (_, _) => Activate();
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private void ApplySize()
    {
        var width = Math.Max(1, (int)Math.Round(_image.PixelWidth * _zoom));
        var height = Math.Max(1, (int)Math.Round(_image.PixelHeight * _zoom));
        SetWindowPos(Handle, IntPtr.Zero, _x, _y, width, height, 0x0004 | 0x0010 /* NOZORDER | NOACTIVATE */);
        RenderOptions.SetBitmapScalingMode(PinImage,
            Math.Abs(_zoom - 1.0) < 0.001 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void SetZoom(double zoom)
    {
        // 拖动后位置可能已变，先读回当前左上角
        if (GetWindowRect(Handle, out var rect))
        {
            _x = rect.Left;
            _y = rect.Top;
        }
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplySize();
        ShowLabel($"{_zoom:P0}");
    }

    private void SetPinOpacity(double opacity)
    {
        Opacity = Math.Clamp(opacity, MinOpacity, 1.0);
        ShowLabel($"透明度 {Opacity:P0}");
    }

    private void ShowLabel(string text)
    {
        ZoomText.Text = text;
        ZoomLabel.Visibility = Visibility.Visible;
        _labelTimer.Stop();
        _labelTimer.Start();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
            Close();
        else
            DragMove();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var up = e.Delta > 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            SetPinOpacity(Opacity + (up ? 0.1 : -0.1));
        else
            SetZoom(up ? _zoom * ZoomStep : _zoom / ZoomStep);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape)
            Close();
        else if (ctrl && e.Key == Key.C)
            ImageActions.Copy(_image);
        else if (ctrl && e.Key == Key.S)
            ImageActions.Save(_image, this);
        else if (ctrl && e.Key is Key.D0 or Key.NumPad0)
            SetZoom(1.0);
    }

    private void Ocr_Click(object sender, RoutedEventArgs e) => App.Instance.RecognizeImage(_image, translate: false);
    private void Translate_Click(object sender, RoutedEventArgs e) => App.Instance.RecognizeImage(_image, translate: true);
    private void Copy_Click(object sender, RoutedEventArgs e) => ImageActions.Copy(_image);
    private void Save_Click(object sender, RoutedEventArgs e) => ImageActions.Save(_image, this);
    private void ResetZoom_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Opacity_Click(object sender, RoutedEventArgs e) =>
        SetPinOpacity(double.Parse((string)((MenuItem)sender).Tag, CultureInfo.InvariantCulture));

    private static IntPtr IgnoreDpiChange(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DPICHANGED)
            handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
