using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;

namespace StickyTodo.App.ViewModels;

/// <summary>看板列表里的一行。</summary>
public sealed class BoardRowViewModel
{
    public BoardRowViewModel(Board board, int pending, int done)
    {
        Board = board;
        Pending = pending;
        Done = done;
    }

    public Board Board { get; }

    public string Name => Board.Name;

    public int Pending { get; }

    public int Done { get; }

    public string Summary => $"{Pending} 待办 · {Done} 已完成";

    public string WindowHint => Board.Window is null ? string.Empty : "已记住窗口位置";
}

/// <summary>管理面板：看板清单 + 增删改名。</summary>
public partial class ManagerViewModel : ObservableObject
{
    private readonly TodoService _service;
    private readonly IEntitlementService _entitlement;

    public ManagerViewModel(TodoService service, IEntitlementService entitlement)
    {
        _service = service;
        _entitlement = entitlement;
    }

    public ObservableCollection<BoardRowViewModel> Boards { get; } = new();

    public string EntitlementBadge =>
        _entitlement.Tier == PlanTier.Lifetime
            ? $"{_entitlement.DisplayName} · 全部功能免费"
            : $"{_entitlement.DisplayName} · 可升级";

    public string StorageHint => "全部数据保存在本机 SQLite，程序不发起任何网络请求";

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private BoardRowViewModel? _selectedBoard;

    public async Task LoadAsync()
    {
        var rows = await Task.Run(() =>
        {
            var list = new List<BoardRowViewModel>();
            foreach (var board in _service.GetBoards())
            {
                var todos = _service.GetTodos(board.Id);
                list.Add(new BoardRowViewModel(
                    board,
                    todos.Count(t => !t.IsDone),
                    todos.Count(t => t.IsDone)));
            }

            return list;
        });

        Boards.Clear();
        foreach (var row in rows)
        {
            Boards.Add(row);
        }
    }

    public async Task<Board?> CreateBoardAsync(string name)
    {
        try
        {
            ErrorText = null;
            var board = await Task.Run(() => _service.CreateBoard(name));
            await LoadAsync();
            return board;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return null;
        }
    }

    public async Task<bool> RenameBoardAsync(Board board, string name)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.RenameBoard(board.Id, name));
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return false;
        }
    }

    public async Task<bool> DeleteBoardAsync(Board board)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.DeleteBoard(board.Id));
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return false;
        }
    }
}