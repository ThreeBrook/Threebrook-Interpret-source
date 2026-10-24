using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ThreebrookInterpret.Services;

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

        // 直接从像素缓冲区建 BitmapSource，只复制一次（原先经 HBITMAP 中转，峰值是三份整屏图）
        var data = bitmap.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var source = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * bounds.Height, data.Stride);
            source.Freeze();
            return (source, bounds);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>截取屏幕上一块区域（物理像素），返回 BGRX 像素（每行 width*4 字节），长截图逐帧用。</summary>
    public static byte[] CaptureRegionPixels(Rectangle region)
    {
        using var bitmap = new Bitmap(region.Width, region.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var target = g.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                BitBlt(target, 0, 0, region.Width, region.Height, screen, region.Left, region.Top, SRCCOPY | CAPTUREBLT);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                g.ReleaseHdc(target);
            }
        }

        var stride = region.Width * 4;
        var pixels = new byte[stride * region.Height];
        var data = bitmap.LockBits(new Rectangle(0, 0, region.Width, region.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            for (var y = 0; y < region.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return pixels;
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
