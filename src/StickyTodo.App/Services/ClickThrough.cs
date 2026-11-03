using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace StickyTodo.App.Services;

/// <summary>
/// 看板锁定的"鼠标穿透"。
///
/// 锁定后给窗口加上 <c>WS_EX_TRANSPARENT</c>，鼠标事件会直接落到下面的窗口/桌面上
/// （看板变成只读的桌面贴纸）；配合 <see cref="Views.UnlockChipWindow"/> 在锁定按钮位置
/// 浮一个小"解锁"徽标，它是独立窗口，所以还能点——这就是"除了锁定按钮其他都穿透"。
/// </summary>
internal static class ClickThrough
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    private static readonly Dictionary<IntPtr, bool> Applied = new();

    /// <summary>开关穿透（窗口必须已经创建句柄）。</summary>
    public static void Apply(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        var hasTransparent = (style & WsExTransparent) != 0;

        if (enabled == hasTransparent)
        {
            return;
        }

        // AllowsTransparency 的窗口本来就是 layered；没有的话补上，否则 TRANSPARENT 不生效
        var target = enabled
            ? style | WsExTransparent | WsExLayered
            : style & ~WsExTransparent;

        SetWindowLong(handle, GwlExStyle, target);
        Applied[handle] = enabled;
    }

    /// <summary>窗口关闭前恢复，避免句柄被复用时样式残留。</summary>
    public static void Reset(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !Applied.ContainsKey(handle))
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style & ~WsExTransparent);
        Applied.Remove(handle);
    }

    /// <summary>元素在屏幕上的物理像素矩形（用于把解锁徽标盖在锁定按钮上）。</summary>
    public static Rect GetScreenRect(FrameworkElement element)
    {
        if (PresentationSource.FromVisual(element) is null || element.ActualWidth < 1)
        {
            return Rect.Empty;
        }

        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>把物理像素换算成 WPF 的 DIP（徽标窗口按 DIP 定位）。</summary>
    public static Rect ToDips(FrameworkElement reference, Rect physical)
    {
        var source = PresentationSource.FromVisual(reference);
        var matrix = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = matrix.Transform(physical.TopLeft);
        var bottomRight = matrix.Transform(physical.BottomRight);
        return new Rect(topLeft, bottomRight);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}