using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ThreeBrookTrans.Services;

public static class ScreenCapture
{
    /// <summary>截取所有显示器组成的虚拟屏幕，返回图像及其物理像素坐标范围。</summary>
    public static (BitmapSource Image, Rectangle Bounds) CaptureVirtualScreen()
    {
        // 进程是 PerMonitorV2，这里拿到的是物理像素
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        // Graphics.CopyFromScreen 不接受 SRCCOPY|CAPTUREBLT 组合，直接调用 BitBlt
        using (var g = Graphics.FromImage(bitmap))
        {
            var target = g.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                BitBlt(target, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SRCCOPY | CAPTUREBLT);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                g.ReleaseHdc(target);
            }
        }

        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return (source, bounds);
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000; // 包含分层（半透明）窗口

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
