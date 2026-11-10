using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using StickyTodo.Core.Services;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 防蓝光滤镜：每个显示器盖一个置顶、点击穿透的纯色半透明窗（传统的叠加滤色路线），
/// 作用在整个屏幕（含其它程序）上。截图 / 贴图不受影响——GDI 抓屏默认抓不到分层窗口。
///
/// 覆盖窗完全不响应输入（WS_EX_TRANSPARENT + WS_EX_NOACTIVATE），不进 Alt-Tab，
/// 不参与会话恢复，随设置开关随时生灭。
/// </summary>
public sealed class EyeCareFilter : IDisposable
{
    private const int HwndTopmost = -1;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly AppSettingsService _settings;
    private readonly Func<Window?> _referenceWindow;
    private readonly List<OverlayEntry> _overlays = new();
    private DispatcherTimer? _topmostTimer;
    private DispatcherTimer? _displayPollTimer;
    private string _lastLayout = string.Empty;
    private bool _disposed;

    private sealed class OverlayEntry
    {
        public Window Window = null!;
        public PixelRect Bounds;
    }

    /// <param name="referenceWindow">
    /// 拿一个已创建的窗口来读显示器列表（Avalonia 的 Screens 挂在窗口上）；
    /// 拿不到时本轮放弃，等下一轮轮询再试。
    /// </param>
    public EyeCareFilter(AppSettingsService settings, Func<Window?> referenceWindow)
    {
        _settings = settings;
        _referenceWindow = referenceWindow;
    }

    public bool IsEnabled => _settings.EyeFilterEnabled;

    /// <summary>预设色板（托盘 / 设置页共用）：颜色 → 文案 key。</summary>
    public static readonly IReadOnlyList<(string Hex, string Key)> PresetList =
    [
        ("#FFC98A", "eye.preset.sunset"),
        ("#FF9F45", "eye.preset.warm"),
        ("#FF8040", "eye.preset.candle"),
        ("#86C88C", "eye.preset.green"),
    ];

    /// <summary>滤镜状态可能变了（托盘勾选态要跟着刷）。</summary>
    public event EventHandler? Changed;

    /// <summary>按当前设置生效或撤销。设置页 / 托盘 / 启动恢复都走这一个入口。</summary>
    public void ApplyFromSettings()
    {
        if (_disposed)
        {
            return;
        }

        if (_settings.EyeFilterEnabled)
        {
            ShowOverlays();
        }
        else
        {
            HideOverlays();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ShowOverlays()
    {
        var color = ParseColor(_settings.EyeFilterColor);
        var opacity = Math.Clamp(_settings.EyeFilterStrength, 10, 50) / 100d;

        try
        {
            if (!EnsureOverlays())
            {
                return;
            }

            foreach (var entry in _overlays)
            {
                entry.Window.Background = new SolidColorBrush(color);
                entry.Window.Opacity = opacity;
                entry.Window.Show();
                PositionOverlay(entry);
            }

            WatchLayout();

            // 顶条带（TOPMOST）内谁被激活谁就到最上面：看板也是置顶的，
            // 激活后会把滤镜压下去，所以定时把滤镜重新顶回去。
            _topmostTimer ??= CreateTopmostTimer();
            _topmostTimer.Start();
        }
        catch (Exception ex)
        {
            LogSafe("[eye] 滤镜覆盖窗创建失败：" + ex.Message);
        }
    }

    private void HideOverlays()
    {
        _topmostTimer?.Stop();
        _displayPollTimer?.Stop();

        foreach (var entry in _overlays.ToList())
        {
            try
            {
                entry.Window.Close();
            }
            catch (Exception)
            {
                // 覆盖窗关不掉不影响主流程
            }
        }

        _overlays.Clear();
    }

    private DispatcherTimer CreateTopmostTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => ReassertTopmost();
        return timer;
    }

    /// <summary>
    /// 滤镜开着时低频轮询显示器布局（Avalonia 没有现成的显示器变更事件）；
    /// 布局变了或上一轮没建起来，就重建。
    /// </summary>
    private void WatchLayout()
    {
        _displayPollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _displayPollTimer.Tick -= OnDisplayPoll;
        _displayPollTimer.Tick += OnDisplayPoll;
        _displayPollTimer.Start();
    }

    private void OnDisplayPoll(object? sender, EventArgs e)
    {
        var layout = DescribeLayout();
        if (layout == _lastLayout && _overlays.Count > 0)
        {
            return;
        }

        _lastLayout = layout;

        if (_settings.EyeFilterEnabled)
        {
            HideOverlays();
            ShowOverlays();
        }
    }

    private string DescribeLayout()
    {
        try
        {
            var reference = _referenceWindow();
            if (reference is null)
            {
                return string.Empty;
            }

            return string.Join("|", reference.Screens.All.Select(s =>
                $"{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}"));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>确保覆盖窗数量与显示器一致；不齐就全关重建。拿不到屏幕列表返回 false。</summary>
    private bool EnsureOverlays()
    {
        PixelRect[] screens;
        try
        {
            var reference = _referenceWindow();
            screens = reference?.Screens.All.Select(s => s.Bounds).ToArray() ?? [];
        }
        catch (Exception)
        {
            return false;
        }

        if (screens.Length == 0)
        {
            return false;
        }

        _lastLayout = string.Join("|", screens.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));

        if (_overlays.Count == screens.Length)
        {
            return true;
        }

        HideOverlays();

        foreach (var bounds in screens)
        {
            var entry = new OverlayEntry { Bounds = bounds };
            entry.Window = CreateOverlayWindow();
            _overlays.Add(entry);
        }

        return true;
    }

    private Window CreateOverlayWindow()
    {
        var overlay = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            Topmost = true,
            Width = 1,
            Height = 1,
            Position = new PixelPoint(0, 0),
        };

        overlay.Opened += (_, _) =>
        {
            var handle = GetHandle(overlay);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            // 点击穿透 + 不进 Alt-Tab + 不抢焦点
            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };

        return overlay;
    }

    /// <summary>按物理像素摆位（多屏 DPI 下最可靠），并压回顶条带。</summary>
    private static void PositionOverlay(OverlayEntry entry)
    {
        var handle = GetHandle(entry.Window);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            HwndTopmost,
            entry.Bounds.X, entry.Bounds.Y, entry.Bounds.Width, entry.Bounds.Height,
            SwpNoActivate | SwpShowWindow);
    }

    private void ReassertTopmost()
    {
        foreach (var entry in _overlays)
        {
            var handle = GetHandle(entry.Window);
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        }
    }

    private static IntPtr GetHandle(Window window)
    {
        try
        {
            return window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static Color ParseColor(string? hex)
    {
        try
        {
            if (hex is not null && Color.Parse(hex) is { } color)
            {
                return color;
            }
        }
        catch (Exception)
        {
            // 颜色写错就回落到默认暖橙
        }

        return Color.FromRgb(0xFF, 0x9F, 0x45);
    }

    private static void LogSafe(string message)
    {
        try
        {
            Log.Info(message);
        }
        catch (Exception)
        {
            // 日志不可用就忽略
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, int after, int x, int y, int cx, int cy, uint flags);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        HideOverlays();
    }
}
