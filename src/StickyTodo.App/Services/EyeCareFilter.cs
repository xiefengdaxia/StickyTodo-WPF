using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using StickyTodo.Core.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>
/// 防蓝光滤镜：每个显示器盖一个置顶、点击穿透的纯色半透明窗（传统的叠加滤色路线），
/// 作用在整个屏幕（含其它程序）上。截图 / 贴图不受影响——GDI 抓屏默认抓不到分层窗口。
///
/// 覆盖窗本身完全不响应输入（WS_EX_TRANSPARENT + WS_EX_NOACTIVATE），
/// 也不进 Alt-Tab、不进会话恢复，随设置开关随时生灭。
/// </summary>
public sealed class EyeCareFilter : IDisposable
{
    private const int HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly AppSettingsService _settings;
    private readonly List<OverlayEntry> _overlays = new();
    private DispatcherTimer? _topmostTimer;
    private bool _displayChangeHooked;
    private bool _disposed;

    private sealed class OverlayEntry
    {
        public Window Window = null!;
        public Drawing.Rectangle Bounds;
    }

    // 屏幕枚举沿用托盘用的 WinForms Screen，口径一致
    private WinForms.Screen[] Screens => WinForms.Screen.AllScreens;

    public EyeCareFilter(AppSettingsService settings)
    {
        _settings = settings;
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
    }

    private void ShowOverlays()
    {
        var color = ParseColor(_settings.EyeFilterColor);
        var opacity = Math.Clamp(_settings.EyeFilterStrength, 10, 50) / 100d;

        try
        {
            EnsureOverlays();

            foreach (var entry in _overlays)
            {
                entry.Window.Background = new SolidColorBrush(color);
                entry.Window.Opacity = opacity;
                entry.Window.Show();
                PositionOverlay(entry);
            }

            // 顶条带（TOPMOST）内谁被激活谁就到最上面：看板也是置顶的，
            // 激活后会把滤镜压下去，所以定时把滤镜重新顶回去。
            // 一次 no-op SetWindowPos，成本可以忽略。
            _topmostTimer ??= CreateTopmostTimer();
            _topmostTimer.Start();

            if (!_displayChangeHooked)
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                _displayChangeHooked = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[eye] 滤镜覆盖窗创建失败，本轮放弃");
        }
    }

    private void HideOverlays()
    {
        _topmostTimer?.Stop();

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

        if (_displayChangeHooked)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _displayChangeHooked = false;
        }
    }

    private DispatcherTimer CreateTopmostTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => ReassertTopmost();
        return timer;
    }

    /// <summary>确保覆盖窗数量与显示器一致；不一致就全关重建。</summary>
    private void EnsureOverlays()
    {
        var screens = Screens;

        if (_overlays.Count != screens.Length)
        {
            HideOverlays();

            foreach (var screen in screens)
            {
                var entry = new OverlayEntry { Bounds = screen.Bounds };
                entry.Window = CreateOverlayWindow(entry);
                _overlays.Add(entry);
            }
        }
    }

    private Window CreateOverlayWindow(OverlayEntry entry)
    {
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            Width = 1,
            Height = 1,
        };

        overlay.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(overlay).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            // 点击穿透 + 不进 Alt-Tab + 不抢焦点
            ClickThrough.Apply(overlay, enabled: true);
            ClickThrough.HideFromAltTab(overlay);
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate);
        };

        return overlay;
    }

    /// <summary>按物理像素摆位（绕开多屏 DPI 换算，Windows 会自己缩放窗口内容）。</summary>
    private static void PositionOverlay(OverlayEntry entry)
    {
        var handle = new WindowInteropHelper(entry.Window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            HwndTopmost,
            entry.Bounds.Left, entry.Bounds.Top, entry.Bounds.Width, entry.Bounds.Height,
            SwpNoActivate | SwpShowWindow);
    }

    private void ReassertTopmost()
    {
        foreach (var entry in _overlays)
        {
            var handle = new WindowInteropHelper(entry.Window).Handle;
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // 分辨率 / 显示器数量变了：下一拍按新布局重建
        if (_settings.EyeFilterEnabled)
        {
            HideOverlays();
            ShowOverlays();
        }
    }

    private static Color ParseColor(string? hex)
    {
        try
        {
            if (hex is not null && ColorConverter.ConvertFromString(hex) is Color color)
            {
                return color;
            }
        }
        catch (FormatException)
        {
            // 颜色写错就回落到默认暖橙
        }

        return Color.FromRgb(0xFF, 0x9F, 0x45);
    }

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

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
