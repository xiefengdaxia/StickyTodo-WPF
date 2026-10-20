using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyTodo.App.Services;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Services;

namespace StickyTodo.App.ViewModels;

/// <summary>一个看板窗口背后的 ViewModel。所有写操作都走 TodoService。</summary>
public partial class BoardViewModel : ObservableObject, IAppearanceHost
{
    private readonly TodoService _service;
    private readonly AppSettingsService _settings;
    private readonly SkinService _skin;
    private readonly Dictionary<string, Tag> _tagsById = new();
    private readonly Dictionary<string, List<string>> _tagMap = new();
    private List<TodoItem> _all = [];

    public BoardViewModel(TodoService service, Board board, AppSettingsService settings, SkinService skin)
    {
        _service = service;
        _settings = settings;
        _skin = skin;
        Board = board;
        _boardName = board.Name;
        _isTopmost = settings.GetBoardTopmost(board.Id);
        _isLocked = settings.GetBoardLocked(board.Id);
        _windowOpacity = skin.WindowOpacity;
    }

    public Board Board { get; }

    public string BoardId => Board.Id;

    public ObservableCollection<TodoRowViewModel> Rows { get; } = new();

    public ObservableCollection<TagChipViewModel> Chips { get; } = new();

    [ObservableProperty]
    private string _boardName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newTitle = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>是否显示已完成条目。</summary>
    [ObservableProperty]
    private bool _showDone = true;

    /// <summary>窗口置顶（便签默认置顶）。</summary>
    [ObservableProperty]
    private bool _isTopmost;

    /// <summary>锁定后内容区禁用，防止误改。</summary>
    [ObservableProperty]
    private bool _isLocked;

    /// <summary>整窗不透明度（连文字一起），绑定到 Window.Opacity。</summary>
    [ObservableProperty]
    private double _windowOpacity = 1.0;

    public string BoardKindText => BoardKinds.DisplayName(Board.Kind);

    public string CurrentSkinKey => _skin.Current.Key;

    public double CurrentOpacity => _skin.Opacity;

    public double CurrentWindowOpacity => _skin.WindowOpacity;

    public IReadOnlyList<NoteSkin> Skins => NoteSkins.All;

    public void ToggleTopmost()
    {
        IsTopmost = !IsTopmost;
        _settings.SetBoardTopmost(BoardId, IsTopmost);
    }

    public void ToggleLock()
    {
        IsLocked = !IsLocked;
        _settings.SetBoardLocked(BoardId, IsLocked);
    }

    public void SetSkin(string skinKey)
    {
        _skin.SetSkin(skinKey);
        OnPropertyChanged(nameof(CurrentSkinKey));
    }

    public void SetOpacity(double opacity)
    {
        _skin.SetOpacity(opacity);
        OnPropertyChanged(nameof(CurrentOpacity));
    }

    public void SetWindowOpacity(double opacity)
    {
        _skin.SetWindowOpacity(opacity);
        OnPropertyChanged(nameof(CurrentWindowOpacity));
        OnPropertyChanged(nameof(WindowOpacity));
    }

    /// <summary>皮肤被别的窗口改过之后刷新一下绑定值。</summary>
    public void RefreshAppearance()
    {
        OnPropertyChanged(nameof(CurrentSkinKey));
        OnPropertyChanged(nameof(CurrentOpacity));
        OnPropertyChanged(nameof(CurrentWindowOpacity));
        OnPropertyChanged(nameof(WindowOpacity));
    }

    // ---------- 倒数日与提醒 ----------

    public async Task SetDueAsync(TodoRowViewModel row, DateTimeOffset? dueAt)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.SetDueAt(row.Id, dueAt));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public async Task SetRemindAsync(TodoRowViewModel row, DateTimeOffset? remindAt)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.SetRemindAt(row.Id, remindAt));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>提醒弹窗里点「稍后」。</summary>
    public void Snooze(string todoId, TimeSpan delay) => _service.Snooze(todoId, delay);

    public async Task RenameBoardAsync(string name)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.RenameBoard(BoardId, name));
            Board.Name = name;
            BoardName = name;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public IReadOnlyList<Tag> Tags => _tagsById.Values.OrderBy(t => t.Sort).ToList();

    public async Task LoadAsync()
    {
        var items = await Task.Run(() => _service.GetTodos(BoardId));
        var tagMap = await Task.Run(() => _service.GetTodoTagMap(BoardId));
        var tags = await Task.Run(() => _service.GetTags());

        _all = items.ToList();
        _tagMap.Clear();
        foreach (var pair in tagMap)
        {
            _tagMap[pair.Key] = pair.Value.ToList();
        }

        _tagsById.Clear();
        foreach (var tag in tags)
        {
            _tagsById[tag.Id] = tag;
        }

        OnPropertyChanged(nameof(Tags));

        if (string.IsNullOrWhiteSpace(BoardName) || BoardName != Board.Name)
        {
            BoardName = Board.Name;
        }

        RebuildChips();
        RebuildRows();
    }

    private void RebuildChips()
    {
        var previous = Chips.FirstOrDefault(c => c.IsSelected)?.TagId;
        Chips.Clear();

        var all = new TagChipViewModel(null, "全部", TagColors.Default) { Count = _all.Count };
        Chips.Add(all);
        var noTag = new TagChipViewModel(TagChipViewModel.NoTagId, "未分类", TagColors.Default)
        {
            Count = _all.Count(t => !_tagMap.TryGetValue(t.Id, out var ids) || ids.Count == 0),
        };
        Chips.Add(noTag);

        foreach (var tag in _tagsById.Values.OrderBy(t => t.Sort))
        {
            Chips.Add(new TagChipViewModel(tag.Id, tag.Name, tag.Color)
            {
                Count = _all.Count(t => _tagMap.TryGetValue(t.Id, out var ids) && ids.Contains(tag.Id)),
            });
        }

        var target = previous is null ? all : Chips.FirstOrDefault(c => c.TagId == previous) ?? all;
        target.IsSelected = true;
    }

    private TagChipViewModel SelectedChip => Chips.FirstOrDefault(c => c.IsSelected) ?? Chips[0];

    private void RebuildRows()
    {
        var chip = SelectedChip;
        var filtered = _all.Where(Matches);
        if (!ShowDone)
        {
            filtered = filtered.Where(t => !t.IsDone);
        }

        Rows.Clear();
        var now = DateTimeOffset.Now;
        foreach (var item in filtered)
        {
            Rows.Add(new TodoRowViewModel(item, TagTextOf(item.Id), now));
        }

        var scoped = _all.Where(Matches).ToList();
        var undone = scoped.Count(t => !t.IsDone);
        var filterName = chip.IsAll ? string.Empty : $"（{chip.Name}）";
        StatusText = ShowDone
            ? $"待办 {undone} · 已完成 {scoped.Count - undone}{filterName}"
            : $"待办 {undone}{filterName}";

        return;

        bool Matches(TodoItem item) => chip.TagId switch
        {
            null => true,
            TagChipViewModel.NoTagId => !_tagMap.TryGetValue(item.Id, out var ids) || ids.Count == 0,
            _ => _tagMap.TryGetValue(item.Id, out var ids) && ids.Contains(chip.TagId),
        };
    }

    private string TagTextOf(string todoId)
    {
        if (!_tagMap.TryGetValue(todoId, out var ids) || ids.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(" · ", ids
            .Where(id => _tagsById.ContainsKey(id))
            .Select(id => _tagsById[id].Name));
    }

    partial void OnShowDoneChanged(bool value) => RebuildRows();

    public async Task SelectChipAsync(TagChipViewModel chip)
    {
        foreach (var item in Chips)
        {
            item.IsSelected = ReferenceEquals(item, chip);
        }

        RebuildRows();
        await Task.CompletedTask;
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewTitle);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var title = NewTitle?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        try
        {
            ErrorText = null;
            await Task.Run(() => _service.AddTodo(BoardId, title));
            NewTitle = string.Empty;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    public async Task ToggleAsync(TodoRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            ErrorText = null;
            await Task.Run(() => _service.SetDone(row.Id, !row.IsDone));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    public async Task DeleteAsync(TodoRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            ErrorText = null;
            await Task.Run(() => _service.Delete(row.Id));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>拖拽落点：把 movingId 放到 targetId 之前，null 表示末尾。</summary>
    public async Task MoveBeforeAsync(string movingId, string? targetId)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.MoveBefore(BoardId, movingId, targetId));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public async Task CreateTagAsync(string name)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.CreateTag(name));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public async Task RenameTagAsync(Tag tag, string name)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.RenameTag(tag.Id, name));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public async Task DeleteTagAsync(Tag tag)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.DeleteTag(tag.Id));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public bool HasTag(TodoRowViewModel row, Tag tag) =>
        _tagMap.TryGetValue(row.Id, out var ids) && ids.Contains(tag.Id);

    public async Task ToggleTagAsync(TodoRowViewModel row, Tag tag)
    {
        try
        {
            ErrorText = null;
            var ids = _tagMap.TryGetValue(row.Id, out var current) ? current.ToList() : [];
            if (!ids.Remove(tag.Id))
            {
                ids.Add(tag.Id);
            }

            await Task.Run(() => _service.SetTodoTags(row.Id, ids));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }
}