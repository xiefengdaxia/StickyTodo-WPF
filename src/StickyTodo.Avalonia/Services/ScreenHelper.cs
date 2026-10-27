using System.Runtime.InteropServices;
using StickyTodo.Core.Geometry;

namespace StickyTodo.Notes.Services;

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

/// <summary>
/// 无边框窗口需要自己算边缘拖拽，这里用到的最小 Win32 集合。
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(nint hWnd, ref NativePoint point);
}

/// <summary>
/// 屏幕工作区换算（设备无关单位），供吸附使用。
/// Avalonia 的 Screen 直接给逻辑像素，比 WPF 少一层矩阵换算。
/// </summary>
public static class ScreenHelper
{
    /// <summary>窗口所在屏幕的工作区（逻辑像素）。</summary>
    public static SnapRect GetWorkArea(Avalonia.Controls.Window window)
    {
        var screens = window.Screens;
        var screen = screens.ScreenFromWindow(window) ?? screens.Primary;

        if (screen is null)
        {
            return new SnapRect(window.Position.X, window.Position.Y, window.Width, window.Height);
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;

        return new SnapRect(
            area.X / scale,
            area.Y / scale,
            area.Width / scale,
            area.Height / scale);
    }

    /// <summary>把窗口夹回可见区域，避免换显示器后窗口跑到屏幕外。</summary>
    public static (double Left, double Top) ClampIntoWorkArea(
        Avalonia.Controls.Window window,
        double left,
        double top,
        double width,
        double height)
    {
        var area = GetWorkArea(window);
        var x = Math.Min(Math.Max(left, area.Left), Math.Max(area.Left, area.Right - width));
        var y = Math.Min(Math.Max(top, area.Top), Math.Max(area.Top, area.Bottom - height));
        return (x, y);
    }

    /// <summary>
    /// 把窗口收进工作区并水平居中：小屏 / 高缩放下（比如 1080p + 150% 只有 720 逻辑高）
    /// 默认尺寸的大窗口会把标题栏顶到屏幕外，看起来就像"拖不动"。
    /// </summary>
    public static void FitIntoWorkArea(Avalonia.Controls.Window window, bool centerHorizontally = true)
    {
        var area = GetWorkArea(window);

        var width = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
        var height = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
        if (double.IsNaN(width) || width <= 0)
        {
            width = area.Width;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = area.Height;
        }

        // 允许缩到的下限：再小标题栏就叠到内容上了
        var maxW = Math.Max(320, area.Width - 16);
        var maxH = Math.Max(360, area.Height - 16);

        if (width > maxW)
        {
            window.Width = maxW;
            width = maxW;
        }

        if (height > maxH)
        {
            window.Height = maxH;
            height = maxH;
        }

        var left = centerHorizontally ? area.Left + (area.Width - width) / 2 : window.Bounds.X;
        var top = area.Top + Math.Max(0, (area.Height - height) / 2);
        var (clampedLeft, clampedTop) = ClampIntoWorkArea(window, left, top, width, height);

        // Avalonia 的 Window.Position 是物理像素，需要按缩放系数换算
        var scale = window.Screens?.ScreenFromWindow(window)?.Scaling is { } s and > 0 ? s : 1.0;
        window.Position = new Avalonia.PixelPoint(
            (int)Math.Round(clampedLeft * scale),
            (int)Math.Round(clampedTop * scale));
    }
}
