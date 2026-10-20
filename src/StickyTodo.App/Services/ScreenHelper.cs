using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using StickyTodo.Core.Geometry;
using WinForms = System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>把屏幕工作区换算成 WPF 设备无关单位，供吸附使用。</summary>
public static class ScreenHelper
{
    public static SnapRect GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var screen = handle != IntPtr.Zero
            ? WinForms.Screen.FromHandle(handle)
            : WinForms.Screen.PrimaryScreen;

        if (screen is null)
        {
            return new SnapRect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        }

        var area = screen.WorkingArea;
        var source = PresentationSource.FromVisual(window);
        var matrix = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        var topLeft = matrix.Transform(new Point(area.Left, area.Top));
        var bottomRight = matrix.Transform(new Point(area.Right, area.Bottom));

        return new SnapRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    /// <summary>把窗口夹回可见区域，避免换显示器后窗口跑到屏幕外。</summary>
    public static (double Left, double Top) ClampIntoWorkArea(Window window, double left, double top, double width, double height)
    {
        var area = GetWorkArea(window);
        var x = Math.Min(Math.Max(left, area.Left), Math.Max(area.Left, area.Right - width));
        var y = Math.Min(Math.Max(top, area.Top), Math.Max(area.Top, area.Bottom - height));
        return (x, y);
    }
}