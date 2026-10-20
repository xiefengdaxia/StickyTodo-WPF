using System.Runtime.InteropServices;

namespace StickyTodo.App.Services;

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

/// <summary>
/// 无边框窗口需要自己算边缘拖拽，这里用到的最小 Win32 集合。
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(nint hWnd, ref NativePoint point);
}