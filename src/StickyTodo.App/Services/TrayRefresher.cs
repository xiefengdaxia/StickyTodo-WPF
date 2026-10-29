using System.Runtime.InteropServices;

namespace StickyTodo.App.Services;

/// <summary>
/// 清理"上一个进程残留的托盘图标"。
///
/// 程序被强杀（任务管理器结束进程 / 崩溃 / 开发时反复 Stop-Process）时来不及删除图标，
/// Windows 会一直把它留在托盘里，直到鼠标从上面划过才消失。启动时主动给托盘窗口
/// 发一批 WM_MOUSEMOVE，让 Explorer 立刻重画托盘区，残留图标当场清掉。
///
/// 做法参考 https://www.cnblogs.com/boxy/p/11402966.html，但**不能写死窗口层级**：
/// 文章里的 Shell_TrayWnd → TrayNotifyWnd → SysPager → ToolbarWindow32 是 Win7~10 的结构；
/// Win11 23H2 实测 SysPager 下面已经没有工具栏了，真正装着图标的是 TrayNotifyWnd 下
/// 另一个 ToolbarWindow32。所以这里改成"枚举所有 ToolbarWindow32，只刷尺寸非零的那个"。
/// </summary>
internal static class TrayRefresher
{
    private const uint WmMouseMove = 0x0200;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoNormal = 0x0000;

    /// <summary>
    /// 刷新托盘显示区与"隐藏图标"溢出区。失败不影响启动。
    /// 放到后台线程：跨进程 SendMessage 很慢（溢出区 200×200 铺一遍要上百次），
    /// 在 UI 线程上做会把启动卡住。
    /// </summary>
    public static void Refresh()
    {
        _ = Task.Run(SweepAll);
    }

    private static void SweepAll()
    {
        var swept = 0;

        try
        {
            // 显示区：整个任务栏里所有 ToolbarWindow32（Win10 在 SysPager 下，Win11 在 TrayNotifyWnd 下）
            var shell = FindWindow("Shell_TrayWnd", null);
            if (shell != nint.Zero)
            {
                swept += SweepToolbarsUnder(shell);
            }

            // 溢出区（点 ^ 展开的那块）
            var overflow = FindWindowEx(nint.Zero, nint.Zero, "NotifyIconOverflowWindow", null);
            if (overflow != nint.Zero)
            {
                swept += SweepToolbarsUnder(overflow);
            }
        }
        catch (Exception)
        {
            // 托盘结构随系统版本变化，刷不到就算了
        }

        Serilog.Log.Information("[tray] 托盘区刷新：扫了 {Count} 个工具栏（清掉上次异常退出留下的图标）", swept);
    }

    /// <summary>枚举 root 下所有 ToolbarWindow32 并逐个刷新，返回实际刷了几个。</summary>
    private static int SweepToolbarsUnder(nint root)
    {
        var count = 0;

        _ = EnumChildWindows(root, (hwnd, _) =>
        {
            if (GetClassNameOf(hwnd) == "ToolbarWindow32" && Sweep(hwnd))
            {
                count++;
            }

            return true;
        }, nint.Zero);

        return count;
    }

    /// <summary>按 10 像素步长铺一遍鼠标经过消息：托盘项收到 WM_MOUSEMOVE 就会重画。</summary>
    private static bool Sweep(nint window)
    {
        if (!GetClientRect(window, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
        {
            // 0x0 的是 Win11 上那个被"架空"的旧工具栏，刷它没有意义
            return false;
        }

        // 托盘图标最小也有 16~24px，10px 的步长足够扫到每一个，又把消息数压到几百条
        for (var x = 0; x < rect.Right; x += 10)
        {
            for (var y = 0; y < rect.Bottom; y += 10)
            {
                // 用带超时的版本：Explorer 卡住时不至于把我们的启动也拖死
                _ = SendMessageTimeout(
                    window,
                    WmMouseMove,
                    nint.Zero,
                    (y << 16) | (x & 0xFFFF),
                    SmtoAbortIfHung | SmtoNormal,
                    20,
                    out _);
            }
        }

        return true;
    }

    private static string GetClassNameOf(nint hwnd)
    {
        var buffer = new char[128];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, char[] buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
