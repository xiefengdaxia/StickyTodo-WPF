using System.Windows;
using StickyTodo.App.ViewModels;
using StickyTodo.App.Views;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Geometry;
using StickyTodo.Core.Services;
using WinForms = System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>便签窗口的公共能力（待办看板 / 便签看板都要）。</summary>
public interface IStickyWindow
{
    string BoardId { get; }

    void ToggleLock();

    void ShowAndActivate();
}

/// <summary>
/// 便签窗口的生命周期：一个看板最多一个窗口，重复打开就激活已有窗口。
/// 同时给吸附算法提供其它窗口的矩形，并记住最后活跃的看板（给全局快捷键用）。
/// </summary>
public sealed class WindowManager
{
    private readonly TodoService _todoService;
    private readonly AppSettingsService _settings;
    private readonly SkinService _skinService;
    private readonly Core.Abstractions.ITokenStatsProvider _tokenProvider;
    private readonly Weather.WeatherService _weatherService;
    private readonly Weather.WeatherCache _weatherCache;
    private readonly Dictionary<string, Window> _windows = new();

    private string? _activeBoardId;

    /// <summary>恢复上次会话期间不要反复写盘。</summary>
    private bool _suspendSessionSave;

    /// <summary>程序正在退出：此时关窗口不应把会话记录清空。</summary>
    private bool _shuttingDown;

    /// <summary>
    /// 会话记录：本次运行中"曾经打开过"的看板，按打开顺序。
    /// 用户主动关掉某个看板时移除它；程序退出导致的一连串关窗则原样保留。
    /// 用这个集合而不是直接看 _windows，是因为退出时 WPF 会把窗口逐个关掉。
    /// </summary>
    private readonly List<string> _sessionBoards = new();

    public WindowManager(
        TodoService todoService,
        AppSettingsService settings,
        SkinService skinService,
        Core.Abstractions.ITokenStatsProvider tokenProvider,
        Weather.WeatherService weatherService,
        Weather.WeatherCache weatherCache)
    {
        _todoService = todoService;
        _settings = settings;
        _skinService = skinService;
        _tokenProvider = tokenProvider;
        _weatherService = weatherService;
        _weatherCache = weatherCache;
    }

    /// <summary>打开的窗口数变化时触发（托盘菜单据此刷新）。</summary>
    public event EventHandler? BoardsChanged;

    public int OpenWindowCount => _windows.Count;

    public bool IsBoardOpen(string boardId) => _windows.ContainsKey(boardId);

    public string? ActiveBoardId => _activeBoardId;

    public Window OpenBoard(Board board)
    {
        if (_windows.TryGetValue(board.Id, out var existing))
        {
            Activate(existing);
            return existing;
        }
        Window window = board.Kind switch
        {
            BoardKind.Memo => new MemoBoardWindow(
                new MemoBoardViewModel(_todoService, board, _settings, _skinService), this),
            BoardKind.Calendar => new CalendarWindow(
                new CalendarViewModel(_todoService, board, _settings, _skinService), this),
            BoardKind.Token => new TokenBoardWindow(
                new TokenBoardViewModel(_tokenProvider, _todoService, board, _settings, _skinService), this),
            // 工具型看板：面板本身不带待办数据，但它是正式看板（进列表、可会话恢复、可重命名/删除）
            BoardKind.Weather => new WeatherWindow(
                new WeatherViewModel(_weatherService, _settings, _weatherCache), board, this),
            BoardKind.WorldClock => new WorldClockWindow(new WorldClockViewModel(), board, this),
            BoardKind.DesktopClock => new DesktopClockWindow(
                new DesktopClockViewModel(_weatherService, _settings, _todoService, this, _tokenProvider, _weatherCache),
                board,
                this),
            BoardKind.NetworkSpeed => new NetworkSpeedWindow(new NetworkSpeedViewModel(), board, this),
            BoardKind.Performance => new PerformanceWindow(new PerformanceViewModel(), board, this),
            _ => new BoardWindow(new BoardViewModel(_todoService, board, _settings, _skinService), this),
        };

        _windows[board.Id] = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(board.Id);
            BoardsChanged?.Invoke(this, EventArgs.Empty);

            // 用户主动关掉这个看板：从会话记录里移除。
            // 程序退出时 WPF 会连续关窗，这种情况由 OnExit 里的 _shuttingDown 兜住；
            // 但 Shutdown 过程中这个标志还来不及置位，所以这里再判一次"是不是真的要退出"。
            if (!_shuttingDown)
            {
                _sessionBoards.Remove(board.Id);
                PersistSession();
            }
        };
        window.Activated += (_, _) => _activeBoardId = board.Id;

        window.Show();
        _activeBoardId = board.Id;

        if (!_sessionBoards.Contains(board.Id))
        {
            _sessionBoards.Add(board.Id);
        }

        BoardsChanged?.Invoke(this, EventArgs.Empty);
        PersistSession();
        return window;
    }

    /// <summary>
    /// 按类型打开看板：已经有就激活，没有就按默认名建一个再打开。
    /// 桌面时钟点"天气/今日日程"时走这里——用户不用先去面板里手动建看板。
    /// </summary>
    public Window? OpenBoardByKind(BoardKind kind, string defaultName)
    {
        var board = _todoService.GetBoards().FirstOrDefault(b => b.Kind == kind);

        if (board is null)
        {
            try
            {
                board = _todoService.CreateBoard(defaultName, kind);
            }
            catch (Exception ex)
            {
                // 看板数已达上限之类：打日志就好，别在挂件上弹错误框
                Serilog.Log.Warning(ex, "[board] 自动创建 {Kind} 看板失败", kind);
                return null;
            }
        }

        var alreadyOpen = _windows.ContainsKey(board.Id);
        var window = OpenBoard(board);

        // 日历已经在别的月份：点"今日日程"要能直接看到今天
        if (alreadyOpen && window is CalendarWindow calendar)
        {
            _ = calendar.GoToTodayAsync();
        }

        return window;
    }

    /// <summary>
    /// 按类型打开看板并贴到 <paramref name="anchor"/> 旁边（桌面时钟点天气/日历用）。
    /// </summary>
    public Window? OpenBoardBeside(BoardKind kind, string defaultName, Window? anchor)
    {
        var window = OpenBoardByKind(kind, defaultName);

        if (window is not null && anchor is not null)
        {
            PlaceBeside(window, anchor);
        }

        return window;
    }

    /// <summary>已有的看板：打开（或激活）并贴到 <paramref name="anchor"/> 旁边。</summary>
    public Window OpenBoardBeside(Board board, Window? anchor)
    {
        var window = OpenBoard(board);

        if (anchor is not null)
        {
            PlaceBeside(window, anchor);
        }

        return window;
    }

    /// <summary>
    /// 把 <paramref name="target"/> 贴到 <paramref name="anchor"/> 边上：
    /// 优先放左边（桌面时钟一般在右下角），左边放不下就放右边；底边对齐，最后夹进工作区。
    /// </summary>
    public static void PlaceBeside(Window target, Window anchor)
    {
        var work = SystemParameters.WorkArea;

        var width = target.ActualWidth > 0 ? target.ActualWidth : target.Width;
        var height = target.ActualHeight > 0 ? target.ActualHeight : target.Height;
        if (double.IsNaN(width) || width <= 0)
        {
            width = 760;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = 560;
        }

        const double gap = 12;

        var left = anchor.Left - width - gap;
        if (left < work.Left)
        {
            left = anchor.Left + anchor.ActualWidth + gap;
        }

        left = Math.Min(Math.Max(left, work.Left), Math.Max(work.Left, work.Right - width));

        // 底边和时钟对齐：时钟贴着屏幕底部时看起来最自然
        var top = anchor.Top + anchor.ActualHeight - height;

        // 底边对齐会顶出屏幕上沿（看板比时钟高很多时）：改成和时钟顶部对齐，
        // 别把窗口甩到屏幕最上面去
        if (top < work.Top)
        {
            top = anchor.Top;
        }

        top = Math.Min(Math.Max(top, work.Top), Math.Max(work.Top, work.Bottom - height));

        target.Left = left;
        target.Top = top;
    }

    /// <summary>把会话记录写进设置，下次启动据此恢复。</summary>
    public void PersistSession()
    {
        if (_suspendSessionSave)
        {
            return;
        }

        try
        {
            _settings.OpenBoards = _sessionBoards.ToList();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "保存会话状态失败");
        }
    }

    /// <summary>
    /// 恢复上次会话：把上次开着、且仍然存在的看板重新打开。
    /// 位置与大小由各窗口自己从 <see cref="Board.Window"/> 读回，所以这里只负责"开哪些"。
    /// 返回恢复出来的窗口数。
    /// </summary>
    public int RestoreSession()
    {
        var ids = _settings.OpenBoards;
        Serilog.Log.Information("[session] RestoreSession 读到 {Count} 个 id：{Ids}",
            ids.Count, ids.Count == 0 ? "(空)" : string.Join(",", ids));

        if (ids.Count == 0 || !_settings.RestoreSession)
        {
            return 0;
        }

        _suspendSessionSave = true;
        var restored = 0;

        try
        {
            var boards = _todoService.GetBoards().ToDictionary(b => b.Id);

            foreach (var id in ids)
            {
                if (!boards.TryGetValue(id, out var board))
                {
                    // 看板已被删除，跳过
                    continue;
                }

                try
                {
                    OpenBoard(board);
                    restored++;
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "恢复看板窗口失败：{Board}", board.Name);
                }
            }
        }
        finally
        {
            _suspendSessionSave = false;

            // 恢复完成后把 _sessionBoards 同步成实际打开的那些，再统一落盘
            _sessionBoards.Clear();
            _sessionBoards.AddRange(_windows.Keys);
            PersistSession();
        }

        Serilog.Log.Information("[session] 上次开着 {Wanted} 个看板，实际恢复 {Restored} 个",
            ids.Count, restored);

        return restored;
    }

    public void CloseBoard(string boardId)
    {
        if (_windows.TryGetValue(boardId, out var window))
        {
            window.Close();
        }
    }

    public void CloseAll()
    {
        // 退出程序时把"当时开着哪些"留在记录里，下次启动才能恢复
        _shuttingDown = true;
        Serilog.Log.Debug("[session] CloseAll：置位 shuttingDown，窗口数={Count}", _windows.Count);

        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }
    }

    /// <summary>标记即将退出：之后的关窗不再覆盖会话记录。</summary>
    public void BeginShutdown()
    {
        // 只固化一次。App.OnExit 里还会再调一次，那时窗口已经关完了，
        // 若照常写入就会把刚才记录的有效值覆盖成空列表。
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        // 退出前把当前开着的看板固化下来，之后关窗一律不再改它
        try
        {
            _settings.OpenBoards = _windows.Keys.ToList();
            Serilog.Log.Information("[session] 退出前固化会话：{Count} 个看板", _windows.Count);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "固化会话失败");
        }
    }

    /// <summary>把窗口位置写回数据库（由窗口在拖动/关闭时调用）。</summary>
    public void SaveBoardGeometry(Board board)
    {
        if (board.Window is { } state)
        {
            _todoService.SaveBoardWindow(board.Id, state);
        }
    }

    /// <summary>
    /// 把所有打开的看板 / 工具窗口收回「鼠标所在屏幕」的右上角。
    ///
    /// 换显示器、改分辨率、或者在多屏之间拔插之后，窗口常常落在已经不存在的那块屏幕坐标里，
    /// 用户只看到托盘图标却找不到窗口。托盘菜单里一键就能把它们叫回来。
    ///
    /// 排布规则：贴着工作区右上角往下叠，一列放不下就往左再开一列，列之间留一点间距，
    /// 顺序按窗口打开顺序（等于「我的看板」里的顺序）。
    /// </summary>
    /// <returns>实际移动了几个窗口。</returns>
    public int ResetOpenWindowsToScreen()
    {
        var windows = _windows.Values
            .Where(w => w.IsVisible && w.WindowState != WindowState.Minimized)
            .ToList();

        if (windows.Count == 0)
        {
            return 0;
        }

        // 目标屏幕 = 鼠标所在屏幕（用户的视线就在那儿）；取不到就退回主屏
        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position) ?? WinForms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return 0;
        }

        var area = screen.WorkingArea;
        const int margin = 12;
        const int gap = 10;

        // 当前列的右边界（屏幕像素）与下一个窗口的顶边
        var columnRight = area.Right - margin;
        var y = area.Top + margin;
        var columnWidth = 0;
        var moved = 0;

        foreach (var window in windows)
        {
            var toDevice = DeviceTransform(window);

            var widthPx = Math.Max(1, (int)Math.Round(window.ActualWidth * toDevice.M11));
            var heightPx = Math.Max(1, (int)Math.Round(window.ActualHeight * toDevice.M22));

            // 这一列放不下了：往左开一列（只在已经有窗口的时候换列）
            if (y + heightPx > area.Bottom - margin && y > area.Top + margin)
            {
                columnRight -= columnWidth + gap;
                y = area.Top + margin;
                columnWidth = 0;
            }

            var left = columnRight - widthPx;
            var top = y;

            // 屏幕实在放不下（超宽窗口 / 高缩放）：夹进工作区，别又跑出去
            left = Math.Max(area.Left, Math.Min(left, area.Right - widthPx));
            top = Math.Max(area.Top, Math.Min(top, area.Bottom - heightPx));

            // 屏幕像素 -> 该窗口自己的设备无关单位（多屏不同 DPI 时各算各的）
            var toDip = DeviceToDip(window);
            var dipLeft = toDip.Transform(new Point(left, top));

            window.Left = dipLeft.X;
            window.Top = dipLeft.Y;

            y = top + heightPx + gap;
            columnWidth = Math.Max(columnWidth, widthPx);
            moved++;
        }

        Serilog.Log.Information(
            "[reset] 已把 {Count} 个窗口收回屏幕 {Screen} 右上角（工作区 {L},{T} {W}x{H}）",
            moved, screen.DeviceName,
            area.Left, area.Top, area.Width, area.Height);

        return moved;
    }

    /// <summary>DIP → 屏幕像素（该窗口自己的 DPI）。</summary>
    private static System.Windows.Media.Matrix DeviceTransform(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        return source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
    }

    /// <summary>屏幕像素 → DIP（该窗口自己的 DPI）。</summary>
    private static System.Windows.Media.Matrix DeviceToDip(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        return source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
    }

    /// <summary>其它可见便签窗口的矩形（已按阴影留白内缩），供吸附计算使用。</summary>
    public IReadOnlyList<SnapRect> GetOtherWindowRects(Window exclude, double margin = 0) =>
        _windows.Values
            .Where(w => !ReferenceEquals(w, exclude) && w.IsVisible && w.WindowState == WindowState.Normal)
            .Select(w => new SnapRect(
                w.Left + margin,
                w.Top + margin,
                Math.Max(1, w.ActualWidth - (margin * 2)),
                Math.Max(1, w.ActualHeight - (margin * 2))))
            .ToList();

    /// <summary>皮肤/透明度变了：重刷资源 + 通知各窗口刷新绑定值。</summary>
    public void ApplySkin(string? skinKey = null, double? opacity = null)
    {
        _skinService.Apply(skinKey, opacity);
        NotifyAppearanceChanged();
    }

    public void NotifyAppearanceChanged()
    {
        foreach (var window in _windows.Values)
        {
            switch (window)
            {
                case BoardWindow board:
                    board.RefreshAppearance();
                    break;
                case MemoBoardWindow memo:
                    memo.RefreshAppearance();
                    break;
                case CalendarWindow calendar:
                    calendar.RefreshAppearance();
                    break;
            }
        }
    }

    /// <summary>日历里删除待办；删完让相关窗口各自刷新（各窗口的 LoadAsync 是幂等的）。</summary>
    public void DeleteTodoAndRefresh(string todoId)
    {
        _todoService.Delete(todoId);
    }

    /// <summary>全局快捷键：一键显示/隐藏全部便签。</summary>
    public void ToggleBoardsVisibility()
    {
        if (_windows.Count == 0)
        {
            return;
        }

        var anyVisible = _windows.Values.Any(w => w.IsVisible);
        foreach (var window in _windows.Values)
        {
            if (anyVisible)
            {
                window.Hide();
            }
            else
            {
                window.Show();
            }
        }
    }

    /// <summary>全局快捷键：锁定/解锁最后活跃的便签。</summary>
    public bool ToggleActiveLock()
    {
        if (_activeBoardId is not null && _windows.TryGetValue(_activeBoardId, out var window) && window is IStickyWindow sticky)
        {
            sticky.ToggleLock();
            return true;
        }

        return false;
    }

    /// <summary>全局快捷键：打开默认看板并把焦点给到新增输入框。</summary>
    public void OpenDefaultBoardForNewTodo()
    {
        var board = _todoService.GetBoards().FirstOrDefault(b => b.IsDefault)
            ?? _todoService.EnsureDefaultBoard();

        if (board.Kind == BoardKind.Memo)
        {
            board = _todoService.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Todo)
                ?? _todoService.EnsureDefaultBoard();
        }

        var window = OpenBoard(board);
        if (window is BoardWindow boardWindow)
        {
            boardWindow.FocusNewTodo();
        }
    }

    private static void Activate(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
