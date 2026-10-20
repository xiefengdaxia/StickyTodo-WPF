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
        };
        window.Activated += (_, _) => _activeBoardId = board.Id;

        window.Show();
        _activeBoardId = board.Id;
        BoardsChanged?.Invoke(this, EventArgs.Empty);
        return window;
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
        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
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