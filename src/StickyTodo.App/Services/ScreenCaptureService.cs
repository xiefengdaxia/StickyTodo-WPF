using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>
/// 屏幕抓取（截图功能用）。纯本机 GDI，不联网。
///
/// 抓的是「整个虚拟桌面」：一次抓全屏，之后选区只是从这张图上裁，
/// 这样覆盖层自己不会被抓进去，选区时也不需要再截一次。
/// </summary>
public static class ScreenCaptureService
{
    /// <summary>一块屏幕（物理像素）。多屏录全屏时用来选屏。</summary>
    public readonly record struct ScreenTarget(int Index, Rectangle Bounds, bool IsPrimary, string DeviceName)
    {
        /// <summary>"1920×1080" 这种分辨率文案（× 用乘号，别用字母 x）。</summary>
        public string Resolution => $"{Bounds.Width}×{Bounds.Height}";
    }

    /// <summary>
    /// 枚举所有屏幕（物理像素）。序号从 1 开始、按显示设置里的顺序，
    /// 和「设置 → 显示」里的编号一致，用户不用猜哪块是几号。
    /// </summary>
    public static IReadOnlyList<ScreenTarget> GetScreens()
    {
        var all = WinForms.Screen.AllScreens;
        var screens = new List<ScreenTarget>(all.Length);

        for (var i = 0; i < all.Length; i++)
        {
            var screen = all[i];
            screens.Add(new ScreenTarget(i + 1, screen.Bounds, screen.Primary, screen.DeviceName));
        }

        return screens;
    }

    /// <summary>
    /// 区域所在显示器的 DPI 缩放（1.0 = 100%，1.5 = 150%）。
    ///
    /// GDI 的抓屏（<c>CopyFromScreen</c> / BitBlt）只认**物理像素**：DIP 矩形要乘上这个系数
    /// 才是要抓的那块屏幕。已知比例时优先用 <see cref="SelectionToPixels"/> 的显式比例，
    /// 这里是没有比例时的兜底（拿不到 DPI 就按 100%，和以前的行为一致）。
    /// </summary>
    public static double DpiScaleFor(Rectangle regionPx)
    {
        try
        {
            var rect = new Rect32
            {
                Left = regionPx.Left,
                Top = regionPx.Top,
                Right = regionPx.Right,
                Bottom = regionPx.Bottom,
            };

            var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return 1d;
            }

            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) != 0 || dpiX == 0)
            {
                return 1d;
            }

            return dpiX / 96.0;
        }
        catch (Exception)
        {
            // Win7 没有 Shcore.dll：当作 100% 缩放
            return 1d;
        }
    }

    /// <summary>
    /// 覆盖层上的 DIP 选区 → 屏幕物理像素（录制 / 抓屏要的就是这个矩形）。
    ///
    /// <paramref name="scaleX"/> / <paramref name="scaleY"/> 是「截图像素 ÷ 覆盖层 DIP」的比例，
    /// 覆盖层跨两块缩放不同的屏时它最准；万一覆盖层还没布局完（比例拿不到），
    /// 就退回该区域的显示器 DPI —— 否则 150% 缩放下会录成一个偏小、偏左上角的区域。
    /// 结果再夹进虚拟屏，越界坐标不要送给 <c>CopyFromScreen</c>。
    /// </summary>
    public static Rectangle SelectionToPixels(Rect dipSelection, Rectangle virtualScreen, double scaleX, double scaleY)
    {
        if (!IsUsableScale(scaleX) || !IsUsableScale(scaleY))
        {
            var probe = new Rectangle(
                virtualScreen.Left + (int)Math.Round(dipSelection.X),
                virtualScreen.Top + (int)Math.Round(dipSelection.Y),
                Math.Max(1, (int)Math.Round(dipSelection.Width)),
                Math.Max(1, (int)Math.Round(dipSelection.Height)));

            var fallback = DpiScaleFor(probe);
            scaleX = IsUsableScale(scaleX) ? scaleX : fallback;
            scaleY = IsUsableScale(scaleY) ? scaleY : fallback;
        }

        var x = virtualScreen.Left + (int)Math.Round(dipSelection.X * scaleX);
        var y = virtualScreen.Top + (int)Math.Round(dipSelection.Y * scaleY);
        var width = Math.Max(4, (int)Math.Round(dipSelection.Width * scaleX));
        var height = Math.Max(4, (int)Math.Round(dipSelection.Height * scaleY));

        var right = Math.Min(virtualScreen.Right, x + width);
        var bottom = Math.Min(virtualScreen.Bottom, y + height);
        x = Math.Clamp(x, virtualScreen.Left, virtualScreen.Right);
        y = Math.Clamp(y, virtualScreen.Top, virtualScreen.Bottom);

        return new Rectangle(x, y, Math.Max(4, right - x), Math.Max(4, bottom - y));
    }

    private static bool IsUsableScale(double scale) => !double.IsNaN(scale) && !double.IsInfinity(scale) && scale > 0;

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

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect32 rect, uint flags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
