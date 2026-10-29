using System.Runtime.InteropServices;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 清理"上一个进程残留的托盘图标"。
///
/// 程序被强杀（任务管理器结束进程 / 崩溃 / 开发时反复 Stop-Process）时来不及删除图标，
/// Windows 会一直把它留在托盘里，直到鼠标从上面划过才消失。启动时主动给托盘窗口
/// 发一批 WM_MOUSEMOVE，让 Explorer 立刻重画托盘区，残留图标当场清掉。
///
/// 做法参考 https://www.cnblogs.com/boxy/p/11402966.html
/// </summary>
internal static class TrayRefresher
{
    private const uint WmMouseMove = 0x0200;

    /// <summary>刷新托盘显示区与"隐藏图标"溢出区。失败不影响启动。</summary>
    public static void Refresh()
    {
        try
        {
            // 显示区：Shell_TrayWnd → TrayNotifyWnd → SysPager → ToolbarWindow32
            Sweep(FindEx(FindEx(FindEx(FindEx(IntPtr.Zero, "Shell_TrayWnd"), "TrayNotifyWnd"), "SysPager"), "ToolbarWindow32"));

            // 溢出区（点 ^ 展开的那块）
            Sweep(FindEx(FindEx(IntPtr.Zero, "NotifyIconOverflowWindow"), "ToolbarWindow32"));

            Log.Info("[tray] 已刷新托盘区（清理上次异常退出留下的图标）");
        }
        catch (Exception)
        {
            // 托盘结构随系统版本变化（Win11 已经不是 ToolbarWindow32），刷不到就算了
        }
    }

    private static void Sweep(nint window)
    {
        if (window == IntPtr.Zero || !GetClientRect(window, out var rect) || rect.Right <= 0)
        {
            return;
        }

        // 按 5 像素步长铺一遍鼠标经过消息：托盘项收到 WM_MOUSEMOVE 就会重画
        for (var x = 0; x < rect.Right; x += 5)
        {
            for (var y = 0; y < rect.Bottom; y += 5)
            {
                _ = SendMessage(window, WmMouseMove, IntPtr.Zero, (y << 16) | (x & 0xFFFF));
            }
        }
    }

    private static nint FindEx(nint parent, string className) =>
        FindWindowEx(parent, IntPtr.Zero, className, string.Empty);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string className, string windowTitle);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint handle, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
}
