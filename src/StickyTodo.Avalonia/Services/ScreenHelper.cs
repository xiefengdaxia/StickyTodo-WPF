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
}
