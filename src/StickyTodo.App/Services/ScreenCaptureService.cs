using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace StickyTodo.App.Services;

/// <summary>
/// 屏幕抓取（截图功能用）。纯本机 GDI，不联网。
///
/// 抓的是「整个虚拟桌面」：一次抓全屏，之后选区只是从这张图上裁，
/// 这样覆盖层自己不会被抓进去，选区时也不需要再截一次。
/// </summary>
public static class ScreenCaptureService
{
    /// <summary>虚拟桌面在屏幕像素下的矩形。</summary>
    public static Rectangle GetVirtualScreenBounds()
    {
        return new Rectangle(
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen),
            GetSystemMetrics(SmCyVirtualScreen));
    }

    /// <summary>抓取整个虚拟桌面（多显示器合计）。失败返回 null。</summary>
    public static BitmapSource? CaptureVirtualScreen()
    {
        try
        {
            var bounds = GetVirtualScreenBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            var handle = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(handle);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>裁剪一块（单位：图像像素）。</summary>
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, source.PixelWidth - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, source.PixelHeight - 1));
        var w = Math.Clamp(rect.Width, 1, source.PixelWidth - x);
        var h = Math.Clamp(rect.Height, 1, source.PixelHeight - y);

        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
