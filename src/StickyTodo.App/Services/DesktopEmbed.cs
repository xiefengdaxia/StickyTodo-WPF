using System.Runtime.InteropServices;

namespace StickyTodo.App.Services;

/// <summary>
/// 把桌面时钟窗口"嵌入"桌面层：
///   1. 加 WS_EX_TOOLWINDOW —— 不出现在 Alt-Tab，更像桌面挂件；
///   2. 把窗口挪到 WorkerW（桌面图标层）之后 —— Win+D 显示桌面时不会被盖住/收起。
/// </summary>
internal static class DesktopEmbed
{
    public static void Embed(System.Windows.Window window)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == nint.Zero)
            {
                return;
            }

            var exStyle = GetWindowLong(handle, GwlExStyle);
            _ = SetWindowLong(handle, GwlExStyle, exStyle | WsExToolwindow);

            var worker = FindWorkerW();
            if (worker != nint.Zero)
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

    private static nint FindWorkerW()
    {
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

        return result;
    }

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
