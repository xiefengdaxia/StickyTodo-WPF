using System.Windows;
using StickyTodo.App.ViewModels;
using StickyTodo.App.Views;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Geometry;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Services;

/// <summary>
/// 便签窗口的生命周期：一个看板最多一个窗口，重复打开就激活已有窗口。
/// 同时给吸附算法提供其它窗口的矩形。
/// </summary>
public sealed class WindowManager
{
    private readonly TodoService _todoService;
    private readonly Dictionary<string, BoardWindow> _windows = new();

    public WindowManager(TodoService todoService, AppSettingsService settings, SkinService skinService)
    {
        _todoService = todoService;
        _settings = settings;
        _skinService = skinService;
    }

    private readonly AppSettingsService _settings;
    private readonly SkinService _skinService;

    /// <summary>打开的窗口数变化时触发（托盘菜单据此刷新）。</summary>
    public event EventHandler? BoardsChanged;

    public int OpenWindowCount => _windows.Count;

    public bool IsBoardOpen(string boardId) => _windows.ContainsKey(boardId);

    public BoardWindow OpenBoard(Board board)
    {
        if (_windows.TryGetValue(board.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return existing;
        }

        var viewModel = new BoardViewModel(_todoService, board, _settings, _skinService);
        var window = new BoardWindow(viewModel, this);
        _windows[board.Id] = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(board.Id);
            BoardsChanged?.Invoke(this, EventArgs.Empty);
        };

        window.Show();
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

    /// <summary>把窗口位置写回数据库（由窗口在拖动/关闭时调用）。</summary>
    public void SaveBoardGeometry(Board board)
    {
        if (board.Window is { } state)
        {
            _todoService.SaveBoardWindow(board.Id, state);
        }
    }

    public void CloseAll()
    {
        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
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

    /// <summary>皮肤或透明度变了，通知所有便签刷新菜单勾选状态。</summary>
    public void NotifyAppearanceChanged()
    {
        foreach (var window in _windows.Values)
        {
            window.RefreshAppearance();
        }
    }

    /// <summary>重新套用皮肤资源（改设置、切皮肤后调用）。</summary>
    public void ApplySkin(string? skinKey = null, double? opacity = null)
    {
        _skinService.Apply(skinKey, opacity);
        NotifyAppearanceChanged();
    }
}