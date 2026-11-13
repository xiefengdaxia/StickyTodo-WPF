using System.Runtime.InteropServices;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 窗口 Z 序小工具（Win32）。开机自启后把看板/时钟压到普通窗口下面，
/// 避免一登录就盖住别的程序。
/// </summary>
internal static class DesktopZOrder
{
    /// <summary>把窗口压到所有普通窗口的最下面（不抢焦点、不动位置）。</summary>
    public static void SendToBottom(Avalonia.Controls.Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            _ = SetWindowPos(handle, HwndBottom, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
        }
        catch (Exception)
        {
            // 压不到底层不影响使用
        }
    }

    private static readonly IntPtr HwndBottom = new(1);

    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
