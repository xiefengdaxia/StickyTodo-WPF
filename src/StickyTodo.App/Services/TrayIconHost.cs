using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>
/// 托盘图标（直接调 Win32 <c>Shell_NotifyIcon</c>，带**固定 GUID**）。
///
/// <para>
/// 为什么不用 WinForms 的 <see cref="NotifyIcon"/>：它按「窗口句柄 + uID」登记图标，
/// 进程被强杀后那个窗口句柄就没了，Explorer 却还把图标留在托盘里（"僵尸图标"），
/// 直到鼠标从上面划过才消失 —— 而且 Win11 的托盘已经是 XAML 渲染，
/// 那种"给托盘工具栏发 WM_MOUSEMOVE 逼它重画"的老办法在新系统上根本没有作用面。
/// </para>
/// <para>
/// 用固定 GUID 登记后，同一个 GUID 的 <c>NIM_ADD</c> 会**直接接管旧槽位**：
/// 下次启动时上一次残留的图标被替换成新进程的图标，用户就看不到残留了。
/// </para>
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    /// <summary>本应用固定的托盘图标 GUID（一次生成、永不修改，改了就换槽位了）。</summary>
    private static readonly Guid IconGuid = new("3f0a1c62-8b5d-4a17-9c2e-5d1b7e4a9f31");

    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifInfo = 0x00000010;
    private const int NifGuid = 0x00000040;

    private const int WmTrayCallback = 0x0400 + 2048;   // WM_APP + 2048

    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonDblClk = 0x0203;

    private const int NiifInfo = 0x00000001;

    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly Icon _icon;
    private readonly string _tooltip;
    private readonly Action _onActivate;
    private readonly Action _onMenu;
    private readonly MessageWindow _window;

    private bool _registered;

    public TrayIconHost(Icon icon, string tooltip, Action onActivate, Action onMenu)
    {
        _icon = icon;
        _tooltip = tooltip;
        _onActivate = onActivate;
        _onMenu = onMenu;

        _window = new MessageWindow(this);
        _window.CreateHandle(new CreateParams
        {
            Caption = "StickyTodoTray",
            X = -100,
            Y = -100,
            Width = 1,
            Height = 1,
        });

        Add();
    }

    /// <summary>气泡通知（提醒用，纯本地）。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_registered)
        {
            return;
        }

        var data = CreateData();
        data.uFlags = NifInfo | NifGuid;
        data.szInfoTitle = Shorten(title, 63);
        data.szInfo = Shorten(text, 255);
        data.dwInfoFlags = NiifInfo;

        _ = Shell_NotifyIcon(NimModify, ref data);
    }

    private void Add()
    {
        var data = CreateData();
        data.uFlags = NifMessage | NifIcon | NifTip | NifGuid;
        data.uCallbackMessage = WmTrayCallback;
        data.hIcon = _icon.Handle;
        data.szTip = Shorten(_tooltip, 127);

        _registered = Shell_NotifyIcon(NimAdd, ref data);

        Serilog.Log.Debug("[tray] 托盘图标已登记（GUID 槽位，残留会被接管）：{Ok}", _registered);
    }

    private NotifyIconData CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        guidItem = IconGuid,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Shorten(string? value, int max)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }

    public void Dispose()
    {
        if (_registered)
        {
            var data = CreateData();
            data.uFlags = NifGuid;
            var ok = Shell_NotifyIcon(NimDelete, ref data);
            Serilog.Log.Debug("[tray] 托盘图标注销（NIM_DELETE）：{Ok}", ok);
            _registered = false;
        }

        _window.DestroyHandle();
    }

    /// <summary>接收托盘回调的消息窗口。</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly TrayIconHost _host;

        public MessageWindow(TrayIconHost host) => _host = host;

        protected override void WndProc(ref Message m)
        {
            // 资源管理器重启后要重新登记，否则图标就没了
            if (m.Msg == TaskbarCreated)
            {
                _host._registered = false;
                _host.Add();
            }
            else if (m.Msg == WmTrayCallback)
            {
                switch (m.LParam.ToInt32() & 0xFFFF)
                {
                    case WmRButtonUp:
                    case WmContextMenu:
                        _host._onMenu();
                        break;
                    case WmLButtonDblClk:
                        _host._onActivate();
                        break;
                }
            }

            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        /// <summary>与 uTimeout 共用一块内存：只在 NIM_SETVERSION 时当版本号用，这里不用。</summary>
        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string message);
}
