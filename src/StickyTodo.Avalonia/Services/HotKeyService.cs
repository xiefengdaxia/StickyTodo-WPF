using System.Runtime.InteropServices;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 全局快捷键（RegisterHotKey）。
///
/// Avalonia 没有 HwndSource.AddHook 这种公开的窗口消息钩子，所以这里
/// 自己建一个 message-only 隐藏窗口来收 WM_HOTKEY。好处是与 Avalonia
/// 的窗口生命周期完全解耦：管理面板隐藏、关闭都不影响快捷键。
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int WmDestroy = 0x0002;

    private const int IdNewTodo = 0x6101;
    private const int IdToggleBoards = 0x6102;
    private const int IdLockActive = 0x6103;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkN = 0x4E;
    private const uint VkH = 0x48;
    private const uint VkL = 0x4C;

    public static string NewTodoHotKey => "Ctrl+Alt+N";

    public static string ToggleBoardsHotKey => "Ctrl+Alt+H";

    public static string LockActiveHotKey => "Ctrl+Alt+L";

    private readonly WndProcDelegate _wndProc;
    private nint _hwnd;
    private bool _registered;

    private Action? _onNewTodo;
    private Action? _onToggleBoards;
    private Action? _onLockActive;

    public HotKeyService()
    {
        // 委托必须存字段，否则会被 GC 回收导致回调崩溃
        _wndProc = HandleMessage;
    }

    /// <summary>注册全局快捷键。重复调用只生效一次。</summary>
    public void Attach(Action onNewTodo, Action onToggleBoards, Action onLockActive)
    {
        _onNewTodo = onNewTodo;
        _onToggleBoards = onToggleBoards;
        _onLockActive = onLockActive;

        if (_registered)
        {
            return;
        }

        try
        {
            _hwnd = CreateMessageWindow(_wndProc);
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            Register(IdNewTodo, VkN);
            Register(IdToggleBoards, VkH);
            Register(IdLockActive, VkL);
            _registered = true;
        }
        catch (Exception)
        {
            // 快捷键不是核心功能，注册失败不影响主流程
            _registered = false;
        }
    }

    private void Register(int id, uint key)
    {
        // 被别的程序占用时注册失败，静默跳过，不影响其它快捷键
        RegisterHotKey(_hwnd, id, ModControl | ModAlt | ModNoRepeat, key);
    }

    private nint HandleMessage(nint hwnd, int msg, nint wParam, nint lParam)
    {
        if (msg == WmHotKey)
        {
            switch ((int)wParam)
            {
                case IdNewTodo:
                    _onNewTodo?.Invoke();
                    break;
                case IdToggleBoards:
                    _onToggleBoards?.Invoke();
                    break;
                case IdLockActive:
                    _onLockActive?.Invoke();
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == WmDestroy)
        {
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            UnregisterHotKey(_hwnd, IdNewTodo);
            UnregisterHotKey(_hwnd, IdToggleBoards);
            UnregisterHotKey(_hwnd, IdLockActive);
            DestroyWindow(_hwnd);
        }
        catch (Exception)
        {
            // 退出阶段忽略
        }

        _hwnd = IntPtr.Zero;
        _registered = false;
    }

    // ---------------- message-only 窗口 ----------------

    private const string ClassName = "StickyTodoHotKeyWindow";

    private static nint CreateMessageWindow(WndProcDelegate wndProc)
    {
        var module = GetModuleHandle(null);

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = module,
            lpszClassName = ClassName,
        };

        // 已注册过就忽略错误（多实例场景）
        RegisterClassEx(ref wc);

        // -3 = HWND_MESSAGE：只收消息、不显示
        return CreateWindowEx(0, ClassName, "StickyTodoHotKey", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, module, IntPtr.Zero);
    }

    private delegate nint WndProcDelegate(nint hwnd, int msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
