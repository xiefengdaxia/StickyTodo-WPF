using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 屏幕抓取（MCP 的 take_screenshot 工具用）。纯本机 GDI，不联网。
/// 抓的是「整个虚拟桌面」：多显示器合计一张图，PNG 字节直接返回。
/// （WPF 版还有 DWM 色温反算；Avalonia 版的 gamma 校色发生在显示 LUT，
/// GDI 抓的是合成表面，本来就不带色偏，不需要反算。）
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

    /// <summary>抓取整个虚拟桌面（多显示器合计），PNG 字节。失败返回 null。</summary>
    public static byte[]? CaptureVirtualScreenPng()
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

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
