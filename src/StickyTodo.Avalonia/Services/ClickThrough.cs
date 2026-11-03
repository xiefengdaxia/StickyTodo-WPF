using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 看板锁定的"鼠标穿透"（Avalonia 版）。
///
/// 锁定后给窗口加 <c>WS_EX_TRANSPARENT</c>：鼠标事件直接落到下面那层窗口/桌面，
/// 看板变成只读贴纸。配合 <see cref="Views.UnlockChipWindow"/>（独立小窗口、
/// 盖在原锁定按钮上）实现"除了解锁按钮，其他全部穿透"。
/// </summary>
internal static class ClickThrough
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    public static void Apply(Window window, bool enabled)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        var has = (style & WsExTransparent) != 0;
        if (has == enabled)
        {
            return;
        }

        var target = enabled
            ? style | WsExTransparent | WsExLayered
            : style & ~WsExTransparent;

        SetWindowLong(handle, GwlExStyle, target);
    }

    /// <summary>
    /// 元素在屏幕上的**物理像素**矩形（Avalonia 的 Window.Position 就是物理像素，
    /// 控件 Bounds 是 DIP，这里按 RenderScaling 换算一次）。
    /// </summary>
    public static PixelRect? GetScreenRect(Control control)
    {
        if (control.GetVisualRoot() is not Window window || control.Bounds.Width < 1)
        {
            return null;
        }

        var offset = control.TranslatePoint(new Point(0, 0), window);
        if (offset is null)
        {
            return null;
        }

        var scale = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
        var x = window.Position.X + (int)Math.Round(offset.Value.X * scale);
        var y = window.Position.Y + (int)Math.Round(offset.Value.Y * scale);
        var w = (int)Math.Round(control.Bounds.Width * scale);
        var h = (int)Math.Round(control.Bounds.Height * scale);

        return new PixelRect(x, y, w, h);
    }

    /// <summary>标记成工具窗口（不进 Alt-Tab）。</summary>
    public static void HideFromAltTab(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);
    }

    /// <summary>
    /// 把徽标放到看板正上方（同一 Z 序带内）。徽标刻意不置顶：
    /// 只在"看板露在桌面/前面"时可见可点，被浏览器等窗口盖住就看不见。
    /// </summary>
    public static void PlaceAbove(Window window, Window reference)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var other = reference.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || other == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(handle, other, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}