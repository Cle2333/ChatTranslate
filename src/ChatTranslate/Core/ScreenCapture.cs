using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ChatTranslate.Core;

/// <summary>虚拟屏幕（所有显示器合并）的几何信息，单位物理像素。</summary>
public sealed record VirtualScreenBounds(int X, int Y, int Width, int Height);

/// <summary>
/// 屏幕截图。抓取整个虚拟桌面，再由上层按框选区域裁剪。
///
/// <para>依赖进程为 PerMonitorV2 DPI 感知（见 app.manifest），否则坐标会被系统缩放，
/// 导致在多显示器 / 高 DPI 下截图区域偏移。</para>
/// </summary>
public static class ScreenCapture
{
    public static VirtualScreenBounds GetVirtualScreenBounds() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    /// <summary>抓取整个虚拟屏幕。</summary>
    public static BitmapSource CaptureVirtualScreen()
    {
        var bounds = GetVirtualScreenBounds();
        return Capture(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    /// <summary>抓取指定区域（物理像素，屏幕坐标系）。</summary>
    public static BitmapSource Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"截图区域无效：{width}×{height}");
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("获取屏幕 DC 失败");
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("创建兼容位图失败");
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);

            if (!NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, NativeMethods.SRCCOPY))
            {
                throw new InvalidOperationException("BitBlt 抓屏失败");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>从已抓取的整屏图中裁剪一块（用于框选后的裁剪）。</summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect region)
    {
        var clamped = ClampToImage(source, region);
        var cropped = new CroppedBitmap(source, clamped);
        cropped.Freeze();
        return cropped;
    }

    private static Int32Rect ClampToImage(BitmapSource source, Int32Rect region)
    {
        var x = Math.Clamp(region.X, 0, source.PixelWidth - 1);
        var y = Math.Clamp(region.Y, 0, source.PixelHeight - 1);
        var w = Math.Clamp(region.Width, 1, source.PixelWidth - x);
        var h = Math.Clamp(region.Height, 1, source.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>保存为 PNG 文件（供系统 OCR 以文件方式读取）。</summary>
    public static void SavePng(BitmapSource source, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
