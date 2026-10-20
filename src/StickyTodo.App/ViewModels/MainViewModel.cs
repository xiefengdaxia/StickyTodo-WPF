using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;

namespace StickyTodo.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TodoService _todoService;
    private readonly IEntitlementService _entitlement;
    private Board? _board;

    public MainViewModel(TodoService todoService, IEntitlementService entitlement)
    {
        _todoService = todoService;
        _entitlement = entitlement;
    }

    public ObservableCollection<TodoItem> Todos { get; } = new();

    public string EntitlementBadge =>
        _entitlement.Tier == PlanTier.Lifetime
            ? $"{_entitlement.DisplayName} · 全部功能免费"
            : $"{_entitlement.DisplayName} · 可升级";

    public string BoardName => _board?.Name ?? "我的待办";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newTitle = string.Empty;

    [ObservableProperty]
    private string _statusText = "加载中…";

    [ObservableProperty]
    private string? _errorText;

    public async Task LoadAsync()
    {
        _board = _todoService.EnsureDefaultBoard();
        OnPropertyChanged(nameof(BoardName));
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_board is null)
        {
            return;
        }

        var boardId = _board.Id;
        var items = await Task.Run(() => _todoService.GetTodos(boardId));

        Todos.Clear();
        foreach (var item in items)
        {
            Todos.Add(item);
        }

        var done = items.Count(i => i.IsDone);
        StatusText = $"待办 {items.Count - done} · 已完成 {done}";
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var title = NewTitle?.Trim();
        if (string.IsNullOrEmpty(title) || _board is null)
        {
            return;
        }

        try
        {
            ErrorText = null;
            await Task.Run(() => _todoService.AddTodo(_board.Id, title));
            NewTitle = string.Empty;
            await ReloadAsync();
        }
        catch (EntitlementLimitException ex)
        {
            ErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewTitle);

    [RelayCommand]
    private async Task ToggleDoneAsync(TodoItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            ErrorText = null;
            await Task.Run(() => _todoService.SetDone(item.Id, !item.IsDone));
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(TodoItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            ErrorText = null;
            await Task.Run(() => _todoService.Delete(item.Id));
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }
}