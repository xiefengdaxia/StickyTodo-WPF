using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using StickyTodo.Infrastructure;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 把 WebView2 嵌进 Avalonia：NativeControlHost 建一个子 HWND（自带 WndProc，
/// 收到 WM_SIZE 时同步控制器边界），CoreWebView2Controller 挂到这个 HWND 上。
///
/// 运行时要求：Windows 10/11 一般自带 WebView2 Runtime；Win7 只能用 Chromium 109
/// 时代的旧版运行时。创建失败（缺运行时等）通过 <see cref="Failed"/> 上抛，
/// 由窗口降级为文本显示。
/// </summary>
public sealed class WebView2Host : NativeControlHost
{
    private IntPtr _hwnd;
    private Microsoft.Web.WebView2.Core.CoreWebView2Environment? _environment;
    private Microsoft.Web.WebView2.Core.CoreWebView2Controller? _controller;
    private Microsoft.Web.WebView2.Core.CoreWebView2? _core;
    private bool _initializeStarted;
    private bool _webReady;
    private string? _pendingHtml;
    private double _scaling = 1;

    /// <summary>WebView2 就绪（可以 Navigate / PostMessage）。</summary>
    public event EventHandler? Ready;

    /// <summary>初始化失败（缺运行时等）。</summary>
    public event EventHandler<Exception>? Failed;

    public bool IsReady => _core is not null;

    public WebView2Host()
    {
        // 尺寸变化时同步控制器边界；换显示器（缩放变化）也要跟着算物理像素
        this.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                SyncControllerBounds();
            }
        };
        this.AttachedToVisualTree += (_, _) =>
        {
            if (VisualRoot is TopLevel top)
            {
                _scaling = top.RenderScaling;
                SyncControllerBounds();
            }
        };
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hwnd = Native.CreateChildWindow(parent.Handle, this);
        if (!_initializeStarted)
        {
            _initializeStarted = true;
            _ = InitializeAsync();
        }

        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            _controller?.Close();
        }
        catch (Exception)
        {
        }

        _controller = null;
        _core = null;
        _environment = null;
        _webReady = false;

        if (_hwnd != IntPtr.Zero)
        {
            Native.DestroyChildWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            // 用户数据目录独立放（独立于数据库/日志），多窗口共用
            _environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: AppPaths.WebView2DataDirectory);
            _controller = await _environment.CreateCoreWebView2ControllerAsync(_hwnd);
            _core = _controller.CoreWebView2;
            _core.Settings.AreDefaultContextMenusEnabled = false;

            // 看板是展示面板不是浏览器：弹窗/下载一律拦下
            _core.NewWindowRequested += (_, e) => e.Handled = true;
            _core.DownloadStarting += (_, e) => e.Cancel = true;

            _webReady = true;
            SyncControllerBounds();

            if (_pendingHtml is not null)
            {
                NavigateToString(_pendingHtml);
                _pendingHtml = null;
            }

            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("[custom] WebView2 初始化失败：" + ex.Message);
            Failed?.Invoke(this, ex);
        }
    }

    /// <summary>加载 HTML（WebView2 还没好就先记着，就绪后自动加载）。</summary>
    public void NavigateToString(string html)
    {
        if (_core is not null)
        {
            _core.NavigateToString(html);
        }
        else
        {
            _pendingHtml = html;
        }
    }

    /// <summary>给页面推一条 JSON 消息（自定义看板契约：{type:'data'|'error'|'board', ...}）。</summary>
    public void PostJson(string json)
    {
        try
        {
            _core?.PostWebMessageAsJson(json);
        }
        catch (Exception)
        {
        }
    }

    internal void SyncControllerBoundsFromNative(int widthPx, int heightPx)
    {
        if (_controller is null)
        {
            return;
        }

        _controller.Bounds = new global::System.Drawing.Rectangle(0, 0, Math.Max(1, widthPx), Math.Max(1, heightPx));
    }

    private void SyncControllerBounds()
    {
        if (_controller is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var width = (int)Math.Max(1, Math.Round(Bounds.Width * _scaling));
        var height = (int)Math.Max(1, Math.Round(Bounds.Height * _scaling));
        SyncControllerBoundsFromNative(width, height);
    }

    /// <summary>Win32 子窗口管理（类注册一次、HWND ↔ 实例映射给 WndProc 用）。</summary>
    private static class Native
    {
        private static string? _registeredClass;
        private static readonly Dictionary<IntPtr, WebView2Host> Hosts = new();

        internal static IntPtr CreateChildWindow(IntPtr parent, WebView2Host host)
        {
            var className = RegisterClass();
            var hwnd = CreateWindowEx(
                0,
                className,
                "StickyTodoWebView2",
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                0, 0, 100, 100,
                parent,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero);
            if (hwnd != IntPtr.Zero)
            {
                Hosts[hwnd] = host;
            }

            return hwnd;
        }

        internal static void DestroyChildWindow(IntPtr hwnd)
        {
            Hosts.Remove(hwnd);
            DestroyWindow(hwnd);
        }

        // 委托实例必须一直活着，否则窗口过程指针悬空
        private static readonly WndProcDelegate WndProcThunk = WndProcMethod;

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static string RegisterClass()
        {
            if (_registeredClass is not null)
            {
                return _registeredClass;
            }

            const string name = "StickyTodoWebView2Host";
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcThunk),
                hInstance = GetModuleHandle(null),
                lpszClassName = name,
            };
            _ = RegisterClassEx(ref wc);
            _registeredClass = name;
            return name;
        }

        private static IntPtr WndProcMethod(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // 控制器边界跟 HWND 客户区同步；摆 HWND 本身是 NativeControlHost 的事
            if (msg == WM_SIZE && Hosts.TryGetValue(hwnd, out var host))
            {
                var width = unchecked((short)(long)lParam) & 0xFFFF;
                var height = unchecked((short)((long)lParam >> 16)) & 0xFFFF;
                host.SyncControllerBoundsFromNative(width, height);
            }

            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private const uint WM_SIZE = 0x0005;
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public IntPtr lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
