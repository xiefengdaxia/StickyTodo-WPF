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
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// 把 <paramref name="window"/> 放到 <paramref name="reference"/> 正上方（同一个 Z 序带内）。
    ///
    /// 解锁徽标刻意**不置顶**：这样它只在"桌面/看板露在外面"时可见，被浏览器等窗口盖住就看不见了；
    /// 但同时它必须压在看板之上才点得到，所以每次看板激活都重新摆一次。
    /// </summary>
    public static void PlaceAbove(Window window, Window reference)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var other = new WindowInteropHelper(reference).Handle;
        if (handle == IntPtr.Zero || other == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(handle, other, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>标记成工具窗口：不出现在 Alt-Tab 任务列表里（桌面挂件小徽标不该占一行）。</summary>
    public static void HideFromAltTab(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);
    }

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

    /// <summary>当前是否处于鼠标穿透状态（回归断言用）。</summary>
    public static bool IsTransparent(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && (GetWindowLong(handle, GwlExStyle) & WsExTransparent) != 0;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}