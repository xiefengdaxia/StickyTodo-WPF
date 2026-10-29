using Avalonia;
using Avalonia.Controls;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Geometry;
using StickyTodo.Core.Services;
using StickyTodo.Notes.ViewModels;
using StickyTodo.Notes.Views;

namespace StickyTodo.Notes.Services;

/// <summary>便签窗口的公共能力（待办看板 / 便签看板都要）。</summary>
public interface IStickyWindow
{
    string BoardId { get; }

    void ToggleLock();

    void ShowAndActivate();

    /// <summary>皮肤/透明度变了以后刷新绑定值。</summary>
    void RefreshAppearance();
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
    /// </summary>
    private readonly List<string> _sessionBoards = new();

    public WindowManager(
        TodoService todoService,
        AppSettingsService settings,
        SkinService skinService,
        Core.Abstractions.ITokenStatsProvider tokenProvider,
        Weather.WeatherService? weatherService = null,
        Weather.WeatherCache? weatherCache = null)
    {
        _todoService = todoService;
        _settings = settings;
        _skinService = skinService;
        _tokenProvider = tokenProvider;
        _weatherService = weatherService ?? new Weather.WeatherService();
        _weatherCache = weatherCache ?? new Weather.WeatherCache();
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
            BoardKind.Weather => new WeatherBoardWindow(
                new WeatherViewModel(_weatherService, _settings, _weatherCache)),
            BoardKind.WorldClock => new WorldClockBoardWindow(
                new WorldClockViewModel()),
            BoardKind.DesktopClock => new DesktopClockBoardWindow(
                new DesktopClockViewModel(_weatherService, _settings, _todoService, this, _tokenProvider, _weatherCache)),
            _ => new BoardWindow(new BoardViewModel(_todoService, board, _settings, _skinService), this),
        };

        _windows[board.Id] = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(board.Id);
            BoardsChanged?.Invoke(this, EventArgs.Empty);

            // 用户主动关掉这个看板：从会话记录里移除。
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
                Log.Warn($"[board] 自动创建 {kind} 看板失败：{ex.Message}");
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
    /// Avalonia 的 Position / WorkingArea 是物理像素，Bounds 是 DIP，要乘缩放。
    /// </summary>
    public static void PlaceBeside(Window target, Window anchor)
    {
        var screen = anchor.Screens.ScreenFromWindow(anchor) ?? anchor.Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var work = screen.WorkingArea;

        var width = (target.Bounds.Width > 0 ? target.Bounds.Width : target.Width) * scale;
        var height = (target.Bounds.Height > 0 ? target.Bounds.Height : target.Height) * scale;
        if (double.IsNaN(width) || width <= 0)
        {
            width = 760 * scale;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = 560 * scale;
        }

        var anchorX = (double)anchor.Position.X;
        var anchorY = (double)anchor.Position.Y;
        var anchorWidth = anchor.Bounds.Width * scale;
        var anchorHeight = anchor.Bounds.Height * scale;

        var gap = 12 * scale;

        var left = anchorX - width - gap;
        if (left < work.X)
        {
            left = anchorX + anchorWidth + gap;
        }

        left = Math.Min(Math.Max(left, work.X), Math.Max(work.X, work.X + work.Width - width));

        // 底边和时钟对齐：时钟贴着屏幕底部时看起来最自然
        var top = anchorY + anchorHeight - height;

        // 底边对齐会顶出屏幕上沿（看板比时钟高很多时）：改成和时钟顶部对齐，
        // 别把窗口甩到屏幕最上面去
        if (top < work.Y)
        {
            top = anchorY;
        }

        top = Math.Min(Math.Max(top, work.Y), Math.Max(work.Y, work.Y + work.Height - height));

        target.Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
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
            Log.Debug("保存会话状态失败：" + ex.Message);
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
        Log.Info($"[session] RestoreSession 读到 {ids.Count} 个 id：{(ids.Count == 0 ? "(空)" : string.Join(",", ids))}");

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
                    Log.Warn($"恢复看板窗口失败：{board.Name} - {ex.Message}");
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

        Log.Info($"[session] 上次开着 {ids.Count} 个看板，实际恢复 {restored} 个");
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
        Log.Debug($"[session] CloseAll：置位 shuttingDown，窗口数={_windows.Count}");

        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }
    }

    /// <summary>标记即将退出：之后的关窗不再覆盖会话记录。</summary>
    public void BeginShutdown()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        try
        {
            _settings.OpenBoards = _windows.Keys.ToList();
            Log.Info($"[session] 退出前固化会话：{_windows.Count} 个看板");
        }
        catch (Exception ex)
        {
            Log.Debug("固化会话失败：" + ex.Message);
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

    /// <summary>其它可见便签窗口的矩形（已按阴影留白内缩），供吸附计算使用。</summary>
    public IReadOnlyList<SnapRect> GetOtherWindowRects(Window exclude, double margin = 0) =>
        _windows.Values
            .Where(w => !ReferenceEquals(w, exclude) && w.IsVisible && w.WindowState == WindowState.Normal)
            .Select(w => new SnapRect(
                w.Position.X + margin,
                w.Position.Y + margin,
                Math.Max(1, w.Width - (margin * 2)),
                Math.Max(1, w.Height - (margin * 2))))
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
            if (window is IStickyWindow sticky)
            {
                sticky.RefreshAppearance();
            }
        }
    }

    /// <summary>语言切换后让所有打开的看板重算由 C# 拼出来的文案。</summary>
    public void RefreshLocalizedText()
    {
        foreach (var window in _windows.Values)
        {
            if (window.DataContext is IRefreshLocalized refresh)
            {
                refresh.RefreshLocalizedText();
            }
        }
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
        if (_activeBoardId is not null
            && _windows.TryGetValue(_activeBoardId, out var window)
            && window is IStickyWindow sticky)
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
