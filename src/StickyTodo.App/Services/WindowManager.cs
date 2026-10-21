using System.Windows;
using StickyTodo.App.ViewModels;
using StickyTodo.App.Views;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Geometry;
using StickyTodo.Core.Services;

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

    public WindowManager(TodoService todoService, AppSettingsService settings, SkinService skinService)
    {
        _todoService = todoService;
        _settings = settings;
        _skinService = skinService;
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
