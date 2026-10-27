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

    /// <summary>
    /// 把窗口收进工作区并居中：小屏 / 高缩放下（1080p + 150% 的工作区只有 720 逻辑高）
    /// 默认尺寸的大窗口会把标题栏顶到屏幕外，看起来就像"拖不动"。
    /// </summary>
    public static void FitIntoWorkArea(Window window, bool centerHorizontally = true)
    {
        var area = GetWorkArea(window);

        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

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

        var left = centerHorizontally ? area.Left + (area.Width - width) / 2 : window.Left;
        var top = area.Top + Math.Max(0, (area.Height - height) / 2);
        var (clampedLeft, clampedTop) = ClampIntoWorkArea(window, left, top, width, height);
        window.Left = clampedLeft;
        window.Top = clampedTop;
    }
}