using System.Runtime.InteropServices;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 把桌面时钟窗口"嵌入"桌面层：
///   1. 加 WS_EX_TOOLWINDOW —— 不出现在 Alt-Tab，更像桌面挂件；
///   2. 把窗口挪到 WorkerW（桌面图标层）之后 —— Win+D 显示桌面时不会被盖住/收起。
///
/// Avalonia 拿不到窗口消息钩子，这里在窗口句柄就绪后用 Win32 直接改扩展样式与 Z 序。
/// </summary>
internal static class DesktopEmbed
{
    public static void Embed(Avalonia.Controls.Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            // 1) 加 TOOLWINDOW 扩展样式（去掉 AppWindow，保留原有其它位）
            var exStyle = GetWindowLong(handle, GwlExStyle);
            _ = SetWindowLong(handle, GwlExStyle, exStyle | WsExToolwindow);

            // 2) 找桌面层 WorkerW 并把自己挂到它后面
            var worker = FindWorkerW();
            if (worker != IntPtr.Zero)
            {
                _ = SetWindowPos(handle, worker, 0, 0, 0, 0,
                    SwpNomove | SwpNosize | SwpNoactivate);
            }
        }
        catch (Exception)
        {
            // 嵌入失败不影响使用，只是行为退回普通置顶窗
        }
    }

    /// <summary>找桌面图标层：Progman → SpawnWorkerW 之后的 WorkerW。</summary>
    private static nint FindWorkerW()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // 触发 Progman 生成 WorkerW（消息 0x052C）
        _ = SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SmtoAbortifhung, 1000, out _);

        // 枚举顶层窗口，找紧跟 Progman 之后、类名为 WorkerW 且不含 SHELLDLL 的那个
        nint result = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            var className = GetClassNameOf(h);
            if (className == "WorkerW")
            {
                // SHELLDLL_DefView 在其中一个 WorkerW 里；它后面的那个才是桌面层
                var shell = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shell != IntPtr.Zero)
                {
                    result = FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string GetClassNameOf(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    // ---------------- Win32 ----------------

    private const int GwlExStyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SmtoAbortifhung = 0x0002;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint child, string? className, string? windowName);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, char[] buffer, int maxCount);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
