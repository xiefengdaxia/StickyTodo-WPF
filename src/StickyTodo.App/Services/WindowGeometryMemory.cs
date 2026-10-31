using System.Windows;
using System.Windows.Threading;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Services;

/// <summary>
/// 工具型看板窗口（天气 / 世界时钟 / 桌面时钟）的位置记忆。
///
/// 便签类窗口各自实现了 RestoreGeometry / SaveGeometry，这三个窗口没有，
/// 所以重启后总是回到默认位置（桌面时钟固定贴右下角、天气/世界时钟永远居中）。
/// 这里抽一个共用助手：把位置写进 <see cref="Board.Window"/>（和便签窗口同一张表），
/// 恢复时按当前工作区夹一下，避免换分辨率后窗口跑到屏幕外。
/// </summary>
internal sealed class WindowGeometryMemory
{
    /// <summary>比这更小的尺寸不当作"用户设过的大小"（工具窗口本身可以很小）。</summary>
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

        _window.LocationChanged += (_, _) => Schedule();
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

    /// <summary>
    /// 挂上位置记忆。
    /// </summary>
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

        var area = ScreenHelper.GetWorkArea(_window);

        // 桌面时钟是 SizeToContent="Height"，Window.Height 是 NaN；
        // 而且 SourceInitialized 时还没布局，ActualWidth/Height 都是 0。
        // 所以依次退：实际布局尺寸 → 记录里的尺寸 → 声明尺寸 → 兜底常量。
        var width = _restoreSize && state.Width >= MinRestoreWidth ? state.Width : _window.ActualWidth;
        var height = _restoreSize && state.Height >= MinRestoreHeight ? state.Height : _window.ActualHeight;

        if (!IsFinite(width) || width < 1)
        {
            width = state.Width;
        }

        if (!IsFinite(height) || height < 1)
        {
            height = state.Height;
        }

        if (!IsFinite(width) || width < 1)
        {
            width = _window.Width;
        }

        if (!IsFinite(height) || height < 1)
        {
            height = _window.Height;
        }

        if (!IsFinite(width) || width < 1)
        {
            width = MinRestoreWidth;
        }

        if (!IsFinite(height) || height < 1)
        {
            height = MinRestoreHeight;
        }

        // 换过显示器 / 改过缩放：先保证不比工作区大，再把左上角夹进工作区
        if (width > area.Width)
        {
            width = area.Width;
        }

        if (height > area.Height)
        {
            height = area.Height;
        }

        var (left, top) = ScreenHelper.ClampIntoWorkArea(_window, state.Left, state.Top, width, height);

        Serilog.Log.Debug(
            "[geometry] 恢复 {Board}：记录=({Left},{Top},{Width},{Height}) 实际={ActualWidth}x{ActualHeight} → ({NewLeft},{NewTop})",
            _board.Name, state.Left, state.Top, state.Width, state.Height,
            _window.ActualWidth, _window.ActualHeight, left, top);

        if (_restoreSize)
        {
            _window.Width = width;
            _window.Height = height;
        }

        _window.Left = left;
        _window.Top = top;
        return true;
    }

    /// <summary>
    /// 位置稳定之后才开始跟踪：否则窗口初始化时那几次布局/贴角
    /// 会把"默认位置"当成用户的选择写进库。
    /// </summary>
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

        var width = _window.ActualWidth;
        var height = _window.ActualHeight;
        if (!IsFinite(width) || !IsFinite(height) || width < 1 || height < 1)
        {
            return;
        }

        _board.Window = new BoardWindowState(_window.Left, _window.Top, width, height);
        _windows.SaveBoardGeometry(_board);

        Serilog.Log.Debug("[geometry] 保存 {Board}：({Left},{Top},{Width},{Height})",
            _board.Name, _window.Left, _window.Top, width, height);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
