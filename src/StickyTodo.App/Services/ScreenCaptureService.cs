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

            NeutralizeTemperatureTint(bitmap);

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

    /// <summary>
    /// 色温模式是在 DWM 合成层乘色矩阵的，抓屏会把这份偏色一起抓进来。
    /// 按当前色温反算回去，贴图/剪贴板里的图才是「原色」，贴到屏幕上只暖一次。
    /// </summary>
    private static void NeutralizeTemperatureTint(Bitmap bitmap)
    {
        var (r, g, b) = EyeGammaService.CaptureTint;

        if (r > 0.999 && g > 0.999 && b > 0.999)
        {
            return;
        }

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            var length = Math.Abs(data.Stride) * bitmap.Height;
            var buffer = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, length);

            var invR = 1d / Math.Max(0.05, r);
            var invG = 1d / Math.Max(0.05, g);
            var invB = 1d / Math.Max(0.05, b);

            for (var i = 0; i + 3 < length; i += 4)
            {
                buffer[i] = (byte)Math.Min(255, Math.Round(buffer[i] * invB));
                buffer[i + 1] = (byte)Math.Min(255, Math.Round(buffer[i + 1] * invG));
                buffer[i + 2] = (byte)Math.Min(255, Math.Round(buffer[i + 2] * invR));
            }

            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, length);
        }
        finally
        {
            bitmap.UnlockBits(data);
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
