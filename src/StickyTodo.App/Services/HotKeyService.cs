using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StickyTodo.App.Services;

/// <summary>
/// 全局快捷键（RegisterHotKey）。绑在管理窗口的 HWND 上，窗口隐藏时依然有效。
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int IdNewTodo = 0x6101;
    private const int IdToggleBoards = 0x6102;
    private const int IdLockActive = 0x6103;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkN = 0x4E;
    private const uint VkH = 0x48;
    private const uint VkL = 0x4C;

    private HwndSource? _source;
    private nint _handle;

    public static string NewTodoHotKey => "Ctrl+Alt+N";

    public static string ToggleBoardsHotKey => "Ctrl+Alt+H";

    public static string LockActiveHotKey => "Ctrl+Alt+L";

    public void Attach(Window window, Action onNewTodo, Action onToggleBoards, Action onLockActive)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            return;
        }

        _source = source;
        _handle = source.Handle;
        source.AddHook(WndProc);

        Register(IdNewTodo, VkN);
        Register(IdToggleBoards, VkH);
        Register(IdLockActive, VkL);

        _onNewTodo = onNewTodo;
        _onToggleBoards = onToggleBoards;
        _onLockActive = onLockActive;
    }

    private Action? _onNewTodo;
    private Action? _onToggleBoards;
    private Action? _onLockActive;

    private void Register(int id, uint key)
    {
        // 被别的程序占用时注册失败，静默跳过，不影响其它快捷键
        RegisterHotKey(_handle, id, ModControl | ModAlt | ModNoRepeat, key);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmHotKey)
        {
            return nint.Zero;
        }

        switch ((int)wParam)
        {
            case IdNewTodo:
                _onNewTodo?.Invoke();
                handled = true;
                break;
            case IdToggleBoards:
                _onToggleBoards?.Invoke();
                handled = true;
                break;
            case IdLockActive:
                _onLockActive?.Invoke();
                handled = true;
                break;
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        UnregisterHotKey(_handle, IdNewTodo);
        UnregisterHotKey(_handle, IdToggleBoards);
        UnregisterHotKey(_handle, IdLockActive);

        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = nint.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}