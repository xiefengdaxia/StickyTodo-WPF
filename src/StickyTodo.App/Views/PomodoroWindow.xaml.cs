using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StickyTodo.App;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Pomodoro;

namespace StickyTodo.App.Views;

/// <summary>
/// 番茄时钟窗口：秒级刷新圆环，专注结束弹提示并自动进入休息。
/// 计时状态机在 Core（可单测），这里只负责驱动和提醒。
/// </summary>
public partial class PomodoroWindow : Window
{
    private const double ResizeBorder = 8d;

    private readonly PomodoroViewModel _viewModel;
    private readonly Action<string, string>? _notify;
    private readonly DispatcherTimer _ticker;
    private readonly bool _autoTick;

    private DateTime _lastTick;
    private bool _geometryRestored;

    public PomodoroWindow(PomodoroViewModel viewModel, Action<string, string>? notify = null, bool autoTick = true)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _notify = notify;
        _autoTick = autoTick;
        DataContext = viewModel;

        _ticker = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _ticker.Tick += OnTick;

        Loaded += (_, _) =>
        {
            RestoreGeometry();
            _lastTick = DateTime.UtcNow;
            UpdateRing();
            RefreshRingColor();

            if (_autoTick)
            {
                _ticker.Start();
            }
        };

        Closing += (_, _) =>
        {
            _ticker.Stop();
            _viewModel.Flush();
            SaveGeometry();
        };
    }

    public PomodoroViewModel ViewModel => _viewModel;

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastTick;
        _lastTick = now;

        // 机器休眠回来时别一次补几分钟，最多只补 2 秒
        if (elapsed > TimeSpan.FromSeconds(2))
        {
            elapsed = TimeSpan.FromSeconds(2);
        }

        var finished = _viewModel.Advance(elapsed);
        UpdateRing();
        RefreshRingColor();

        if (finished is not null)
        {
            OnPhaseFinished(finished.Value);
        }
    }

    private void OnPhaseFinished(PomodoroPhase finished)
    {
        var next = PomodoroEngine.PhaseName(_viewModel.Engine.Phase);
        var (title, text) = finished == PomodoroPhase.Focus
            ? ("专注结束", $"休息一下，接下来是{next}")
            : ("休息结束", "回到专注，继续加油");

        _notify?.Invoke(title, text);

        // 休息结束自动开始下一轮专注；专注结束停下来让人决定何时休息
        if (finished != PomodoroPhase.Focus)
        {
            _viewModel.Engine.Start();
            _viewModel.Sync();
        }

        UpdateRing();
        RefreshRingColor();
    }

    /// <summary>给回归用：走窗口真实的"一段结束"处理（含通知与自动继续）。</summary>
    internal void ApplyFinishedForTest(PomodoroPhase phase) => OnPhaseFinished(phase);
    private void UpdateRing()
    {
        var progress = Math.Clamp(_viewModel.Progress, 0, 1);

        // 圆周长 = π * 直径（宽高都是 160 → 半径 80）
        const double radius = 80;
        var circumference = 2 * Math.PI * radius;

        ProgressRing.StrokeDashArray = [circumference * progress, circumference * (1 - progress) + 0.001];
        ProgressRing.StrokeDashOffset = 0;
    }

    /// <summary>专注是强调色，休息换成绿色。</summary>
    private void RefreshRingColor()
    {
        if (_viewModel.IsBreak)
        {
            ProgressRing.Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        }
        else if (TryFindResource("Brush.Accent") is Brush accent)
        {
            ProgressRing.Stroke = accent;
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Toggle();
        _lastTick = DateTime.UtcNow;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Reset();
        _lastTick = DateTime.UtcNow;
        UpdateRing();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Skip();
        _lastTick = DateTime.UtcNow;
        UpdateRing();
        RefreshRingColor();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- 位置记忆 ----------------

    private void RestoreGeometry()
    {
        if (_geometryRestored)
        {
            return;
        }

        _geometryRestored = true;

        // 默认摆在屏幕右侧偏上，不挡主工作区
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 40;
        Top = work.Top + 80;
    }

    private void SaveGeometry()
    {
        // 位置不落库：番茄钟每次开在固定位置更可预期
    }

    // ---------------- 边缘缩放 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != 0x0084 || ResizeMode == ResizeMode.NoResize)
        {
            return nint.Zero;
        }

        long raw = lParam.ToInt64();
        var point = new NativePoint
        {
            X = (int)(short)(raw & 0xFFFF),
            Y = (int)(short)((raw >> 16) & 0xFFFF),
        };

        if (!NativeMethods.ScreenToClient(hwnd, ref point))
        {
            return nint.Zero;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = point.X / dpi.DpiScaleX;
        var y = point.Y / dpi.DpiScaleY;

        var onLeft = x <= ResizeBorder;
        var onRight = x >= ActualWidth - ResizeBorder;
        var onTop = y <= ResizeBorder;
        var onBottom = y >= ActualHeight - ResizeBorder;

        var hit = onTop && onLeft ? 13
            : onTop && onRight ? 14
            : onBottom && onLeft ? 16
            : onBottom && onRight ? 17
            : onLeft ? 10
            : onRight ? 11
            : onTop ? 12
            : onBottom ? 15
            : 0;

        if (hit == 0)
        {
            return nint.Zero;
        }

        handled = true;
        return new nint(hit);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _ticker.Stop();
        base.OnClosing(e);
    }
}