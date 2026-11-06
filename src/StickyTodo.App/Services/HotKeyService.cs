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
    private const int IdSnip = 0x6104;
    private const int IdPinAgain = 0x6105;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkN = 0x4E;
    private const uint VkH = 0x48;
    private const uint VkL = 0x4C;
    private const uint VkF1 = 0x70;
    private const uint VkF3 = 0x72;

    private HwndSource? _source;
    private nint _handle;

    public static string NewTodoHotKey => "Ctrl+Alt+N";

    public static string ToggleBoardsHotKey => "Ctrl+Alt+H";

    public static string LockActiveHotKey => "Ctrl+Alt+L";

    /// <summary>截图（默认 F1）。</summary>
    public static string SnipHotKey => "F1";

    /// <summary>把上一次的截图再贴一张（默认 F3）。</summary>
    public static string PinAgainHotKey => "F3";

    public int Attach(
        Window window,
        Action onNewTodo,
        Action onToggleBoards,
        Action onLockActive,
        Action? onSnip = null,
        Action? onPinAgain = null)
    {
        // 主窗体可能一直没显示过（上次有看板时我们不弹它），这时
        // PresentationSource.FromVisual 是 null，直接 return 会让所有全局快捷键静默失效。
        // 用 EnsureHandle 拿到真实 HWND，再取它的 HwndSource。
        var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(handle);

        if (source is null)
        {
            Serilog.Log.Warning("[hotkey] 拿不到窗口消息源，全局快捷键没有注册");
            return 0;
        }

        _source = source;
        _handle = handle;
        source.AddHook(WndProc);

        var registered = 0;
        registered += Register(IdNewTodo, "新建待办", VkN, ModControl | ModAlt) ? 1 : 0;
        registered += Register(IdToggleBoards, "显示/隐藏", VkH, ModControl | ModAlt) ? 1 : 0;
        registered += Register(IdLockActive, "锁定看板", VkL, ModControl | ModAlt) ? 1 : 0;

        // F1 / F3 不带修饰键
        registered += Register(IdSnip, "截图", VkF1, 0) ? 1 : 0;
        registered += Register(IdPinAgain, "再贴一张", VkF3, 0) ? 1 : 0;

        _onNewTodo = onNewTodo;
        _onToggleBoards = onToggleBoards;
        _onLockActive = onLockActive;
        _onSnip = onSnip;
        _onPinAgain = onPinAgain;

        return registered;
    }

    private Action? _onNewTodo;
    private Action? _onToggleBoards;
    private Action? _onLockActive;
    private Action? _onSnip;
    private Action? _onPinAgain;

    /// <summary>注册一个全局热键；失败（被别的程序占用）时记一条警告，返回 false。</summary>
    private bool Register(int id, string name, uint key, uint modifiers)
    {
        var ok = RegisterHotKey(_handle, id, modifiers | ModNoRepeat, key);

        if (!ok)
        {
            Serilog.Log.Warning(
                "[hotkey] {Name} 注册失败（win32={Error}），通常是已被其它程序占用",
                name, Marshal.GetLastWin32Error());
        }

        return ok;
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
            case IdSnip:
                _onSnip?.Invoke();
                handled = true;
                break;
            case IdPinAgain:
                _onPinAgain?.Invoke();
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
        UnregisterHotKey(_handle, IdSnip);
        UnregisterHotKey(_handle, IdPinAgain);

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