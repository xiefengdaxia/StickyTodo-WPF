using System.Runtime.InteropServices;

namespace StickyTodo.App.Services;

/// <summary>
/// 把桌面时钟窗口"嵌入"桌面层：
///   1. 加 WS_EX_TOOLWINDOW —— 不出现在 Alt-Tab，更像桌面挂件；
///   2. 把窗口挪到 WorkerW（桌面图标层）之后 —— Win+D 显示桌面时不会被盖住/收起。
/// </summary>
internal static class DesktopEmbed
{
    /// <summary>
    /// 尝试嵌入桌面层。
    /// </summary>
    /// <returns>true = 已挂到桌面图标层后面；false = 嵌入失败，调用方应自己退回置顶。</returns>
    public static bool Embed(System.Windows.Window window)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == nint.Zero)
            {
                return false;
            }

            var exStyle = GetWindowLong(handle, GwlExStyle);
            _ = SetWindowLong(handle, GwlExStyle, exStyle | WsExToolwindow);

            var worker = FindWorkerW();
            if (worker == nint.Zero)
            {
                return false;
            }

            // 先脱离置顶带：topmost 窗口没法插到桌面层后面
            _ = SetWindowPos(handle, HwndNotopmost, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNoactivate);

            // 挂到桌面图标层之后：普通窗口都压在它上面，Win+D 也不会收起它
            _ = SetWindowPos(handle, worker, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNoactivate);
            return true;
        }
        catch (Exception)
        {
            // 嵌入失败不影响使用，只是行为退回普通置顶窗
            return false;
        }
    }

    private static nint FindWorkerW()
    {
        // 桌面层进程活着就不会变：缓存起来，避免每次嵌入都给 Progman 发消息（可能卡 UI）
        if (_cachedWorker != nint.Zero && IsWindow(_cachedWorker))
        {
            return _cachedWorker;
        }

        var progman = FindWindow("Progman", null);
        if (progman == nint.Zero)
        {
            return nint.Zero;
        }

        _ = SendMessageTimeout(progman, 0x052C, nint.Zero, nint.Zero, SmtoAbortifhung, 1000, out _);

        nint result = nint.Zero;
        EnumWindows((h, l) =>
        {
            var className = GetClassNameOf(h);
            if (className == "WorkerW")
            {
                var shell = FindWindowEx(h, nint.Zero, "SHELLDLL_DefView", null);
                if (shell != nint.Zero)
                {
                    result = FindWindowEx(nint.Zero, h, "WorkerW", null);
                    return false;
                }
            }

            return true;
        }, nint.Zero);

        _cachedWorker = result;
        return result;
    }

    /// <summary>缓存的桌面层句柄（Explorer 重启后失效，用 IsWindow 校验）。</summary>
    private static nint _cachedWorker;

    private static string GetClassNameOf(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private const int GwlExStyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SmtoAbortifhung = 0x0002;

    /// <summary>退出置顶带（否则 Z 序操作无效，窗口永远盖在所有程序上面）。</summary>
    private static readonly nint HwndNotopmost = new(-2);

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint child, string? className, string? windowName);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, char[] buffer, int maxCount);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
