using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using StickyTodo.Core.Entities;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 工具型看板窗口（天气 / 世界时钟 / 桌面时钟）的位置记忆（Avalonia 版）。
///
/// 便签类窗口走 <c>StickyWindowBase</c> 的 RestoreGeometry / SaveGeometry，
/// 这三个窗口没有，重启后总是回到默认位置。这里用同一张表（<see cref="Board.Window"/>）
/// 存位置，坐标按逻辑单位（DIP）存，恢复时按当前工作区夹一下。
/// </summary>
internal sealed class WindowGeometryMemory
{
    private const double MinRestoreWidth = 200;
    private const double MinRestoreHeight = 160;

    private readonly Window _window;
    private readonly Board _board;
    private readonly WindowManager _windows;
    private readonly bool _restoreSize;
    private readonly DispatcherTimer _saveTimer;
    private bool _armed;

    private WindowGeometryMemory(
        Window window,
        Board board,
        WindowManager windows,
        bool restoreSize,
        bool trackSize)
    {
        _window = window;
        _board = board;
        _windows = windows;
        _restoreSize = restoreSize;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };

        _window.PositionChanged += (_, _) => Schedule();
        if (trackSize)
        {
            _window.SizeChanged += (_, _) => Schedule();
        }

        _window.Closed += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
    }

    /// <param name="restoreSize">是否连宽高一起恢复（SizeToContent 的窗口传 false）。</param>
    /// <param name="trackSize">尺寸变化时是否也存（尺寸由内容决定的窗口传 false）。</param>
    public static WindowGeometryMemory Attach(
        Window window,
        Board board,
        WindowManager windows,
        bool restoreSize,
        bool trackSize = true) =>
        new(window, board, windows, restoreSize, trackSize);

    /// <summary>有记录就把窗口摆回去，返回是否真的恢复过。</summary>
    public bool Restore()
    {
        if (_board.Window is not { } state)
        {
            return false;
        }

        if (!IsFinite(state.Left) || !IsFinite(state.Top))
        {
            return false;
        }

        // 工具窗口改过默认尺寸后，位置记忆里存的还是旧默认值（如性能看板 700、网速看板 640）。
        // 记录值和当前声明尺寸一致 = 用户从未手动拉过 → 用新默认尺寸，别让老默认一直跟着迁移。
        var declaredWidth = IsFinite(_window.Width) ? _window.Width : 0;
        var declaredHeight = IsFinite(_window.Height) ? _window.Height : 0;
        var userResizedWidth = Math.Abs(state.Width - declaredWidth) > 0.5;
        var userResizedHeight = Math.Abs(state.Height - declaredHeight) > 0.5;

        var width = _restoreSize && state.Width >= MinRestoreWidth && userResizedWidth
            ? state.Width
            : ClientWidth();
        var height = _restoreSize && state.Height >= MinRestoreHeight && userResizedHeight
            ? state.Height
            : ClientHeight();

        var area = ScreenHelper.GetWorkArea(_window);

        if (width > area.Width)
        {
            width = area.Width;
        }

        if (height > area.Height)
        {
            height = area.Height;
        }

        var (left, top) = ScreenHelper.ClampIntoWorkArea(_window, state.Left, state.Top, width, height);

        if (_restoreSize)
        {
            _window.Width = width;
            _window.Height = height;
        }

        _window.Position = ToPixel(left, top);
        return true;
    }

    /// <summary>位置稳定之后才开始跟踪，避免初始化阶段的布局把默认位置写进库。</summary>
    public void Arm() => _armed = true;

    private void Schedule()
    {
        if (!_armed)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        if (!_armed || _window.WindowState != WindowState.Normal)
        {
            return;
        }

        var width = _window.Bounds.Width;
        var height = _window.Bounds.Height;
        if (!IsFinite(width) || !IsFinite(height) || width < 1 || height < 1)
        {
            return;
        }

        var position = CurrentPositionInLogicalUnits();
        _board.Window = new BoardWindowState(position.X, position.Y, width, height);
        _windows.SaveBoardGeometry(_board);
    }

    private double ClientWidth() => _window.Bounds.Width > 1
        ? _window.Bounds.Width
        : (_window.Width > 0 ? _window.Width : MinRestoreWidth);

    private double ClientHeight() => _window.Bounds.Height > 1
        ? _window.Bounds.Height
        : (_window.Height > 0 ? _window.Height : MinRestoreHeight);

    private double Scaling()
    {
        var screen = _window.Screens.ScreenFromWindow(_window) ?? _window.Screens.Primary;
        var scale = screen?.Scaling ?? 1;
        return scale <= 0 ? 1 : scale;
    }

    private PixelPoint ToPixel(double left, double top)
    {
        var scale = Scaling();
        return new PixelPoint((int)Math.Round(left * scale), (int)Math.Round(top * scale));
    }

    private Point CurrentPositionInLogicalUnits()
    {
        var scale = Scaling();
        return new Point(_window.Position.X / scale, _window.Position.Y / scale);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
