using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.ViewModels;

/// <summary>看板列表里的一行。</summary>
public sealed class BoardRowViewModel : ObservableObject
{
    public BoardRowViewModel(Board board, int pending, int done)
    {
        Board = board;
        Pending = pending;
        Done = done;
    }

    public Board Board { get; }

    public string Name => Board.Name;

    public string KindText => Loc.T(Board.Kind switch
    {
        BoardKind.Memo => "manager.kindMemo",
        BoardKind.Calendar => "manager.kindCalendar",
        BoardKind.Token => "manager.kindToken",
        _ => "manager.kindTodo",
    });

    /// <summary>卡片左侧的类型图标。</summary>
    public string KindGlyph => Board.Kind switch
    {
        BoardKind.Memo => "✎",
        BoardKind.Calendar => "▦",
        BoardKind.Token => "◎",
        _ => "✓",
    };

    public bool IsMemo => Board.Kind == BoardKind.Memo;

    public bool IsCalendar => Board.Kind == BoardKind.Calendar;

    public bool IsToken => Board.Kind == BoardKind.Token;

    public int Pending { get; }

    public int Done { get; }

    public string Summary => IsMemo
        ? Loc.T("manager.summaryMemo")
        : IsCalendar
            ? Loc.T("manager.summaryCalendar")
            : IsToken
                ? Loc.T("manager.summaryToken")
                : Loc.F("manager.summaryTodo", Pending, Done);

    public string WindowHint => Board.Window is null ? string.Empty : Loc.T("manager.rememberedGeometry");

    /// <summary>语言切换后通知这一行所有拼接文本重算。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(WindowHint));
    }
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

    public bool HasNoBoards => Boards.Count == 0;

    public string EntitlementBadge =>
        _entitlement.Tier == PlanTier.Lifetime
            ? Loc.F("settings.entitlementLifetime", EntitlementName)
            : Loc.F("settings.entitlementLimited", EntitlementName);

    public string StorageHint => Loc.T("manager.storageHint");

    /// <summary>语言切换后重算卡片上由 C# 拼出来的文本。</summary>
    public void RefreshLocalizedText()
    {
        foreach (var row in Boards)
        {
            row.RefreshLocalizedText();
        }

        OnPropertyChanged(nameof(EntitlementBadge));
        OnPropertyChanged(nameof(StorageHint));
        OnPropertyChanged(nameof(FooterHint));
    }

    /// <summary>会员档位显示名（按当前语言）。</summary>
    private string EntitlementName =>
        Loc.T(_entitlement.Tier == PlanTier.Lifetime ? "entitlement.lifetime" : "entitlement.free");

    public string FooterHint =>
        Loc.F("manager.footerHint", HotKeyService.NewTodoHotKey);

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

        OnPropertyChanged(nameof(HasNoBoards));
    }

    public async Task<Board?> CreateBoardAsync(string name, BoardKind kind = BoardKind.Todo)
    {
        try
        {
            ErrorText = null;
            var board = await Task.Run(() => _service.CreateBoard(name, kind));
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
