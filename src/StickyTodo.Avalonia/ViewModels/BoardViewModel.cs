using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.ViewModels;

/// <summary>一个看板窗口背后的 ViewModel。所有写操作都走 TodoService。</summary>
public partial class BoardViewModel : ObservableObject, IAppearanceHost, IRefreshLocalized, ISessionTopmost
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
        _windowOpacity = settings.GetWindowOpacity(board.Id);
        _isQuadrantView = string.Equals(settings.GetBoardView(board.Id), "quadrant", StringComparison.OrdinalIgnoreCase);

        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _undoTimer.Tick += (_, _) => ClearUndoDelete();
    }

    public Board Board { get; }

    public string BoardId => Board.Id;

    public ObservableCollection<TodoRowViewModel> Rows { get; } = new();

    public ObservableCollection<TagChipViewModel> Chips { get; } = new();

    [ObservableProperty]
    private string _boardName;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>
    /// 表格末尾那行「空白行」的文本。在这里回车/失焦即新增一条，
    /// 因此不再需要单独的输入框和「添加」按钮。
    /// </summary>
    [ObservableProperty]
    private string _draftTitle = string.Empty;

    /// <summary>草稿行是否获得焦点（用于渲染更明显的提示）。</summary>
    [ObservableProperty]
    private bool _isDraftFocused;

    /// <summary>
    /// 看板内搜索关键字（标题 / 标签 / 日期文本都能命中）。
    /// 空串表示不过滤；与标签胶囊是「与」的关系。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    [NotifyPropertyChangedFor(nameof(SearchPlaceholder))]
    private string _searchText = string.Empty;

    /// <summary>搜索框是否展开（点标题栏的 🔍 切换）。</summary>
    [ObservableProperty]
    private bool _isSearchOpen;

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    public string SearchPlaceholder => Loc.T("board.searchPlaceholder");

    /// <summary>输入变化时重算可见行；关掉搜索框时清空关键字。</summary>
    partial void OnSearchTextChanged(string value) => SyncRows();

    partial void OnIsSearchOpenChanged(bool value)
    {
        if (!value && HasSearch)
        {
            SearchText = string.Empty;
        }
    }

    /// <summary>给 XAML 的「清除」按钮用：只清关键字，保留搜索框。</summary>
    public void ClearSearch() => SearchText = string.Empty;

    public void ToggleSearch() => IsSearchOpen = !IsSearchOpen;

    /// <summary>当前视图：待办 / 已完成。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTodoView))]
    [NotifyPropertyChangedFor(nameof(IsDoneView))]
    [NotifyPropertyChangedFor(nameof(TodoViewText))]
    [NotifyPropertyChangedFor(nameof(DoneViewText))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyPropertyChangedFor(nameof(DraftPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasNoRows))]
    [NotifyPropertyChangedFor(nameof(ShowQuadrants))]
    private TodoViewMode _viewMode = TodoViewMode.Todo;

    /// <summary>待办视图的呈现方式：列表 / 四象限（只影响「待办」视图）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    [NotifyPropertyChangedFor(nameof(ShowQuadrants))]
    private bool _isQuadrantView;

    public bool IsListView => !IsQuadrantView;

    public bool ShowQuadrants => IsQuadrantView && IsTodoView;

    public string QuadrantToggleTip => Loc.T("view.quadrantTip");

    public ObservableCollection<TodoRowViewModel> Quadrant1Rows { get; } = new();
    public ObservableCollection<TodoRowViewModel> Quadrant2Rows { get; } = new();
    public ObservableCollection<TodoRowViewModel> Quadrant3Rows { get; } = new();
    public ObservableCollection<TodoRowViewModel> Quadrant4Rows { get; } = new();

    public string Quadrant1Title => Loc.T("quadrant.q1");
    public string Quadrant1Subtitle => Loc.T("quadrant.q1Sub");
    public string Quadrant2Title => Loc.T("quadrant.q2");
    public string Quadrant2Subtitle => Loc.T("quadrant.q2Sub");
    public string Quadrant3Title => Loc.T("quadrant.q3");
    public string Quadrant3Subtitle => Loc.T("quadrant.q3Sub");
    public string Quadrant4Title => Loc.T("quadrant.q4");
    public string Quadrant4Subtitle => Loc.T("quadrant.q4Sub");

    public int Quadrant1Count => Quadrant1Rows.Count;
    public int Quadrant2Count => Quadrant2Rows.Count;
    public int Quadrant3Count => Quadrant3Rows.Count;
    public int Quadrant4Count => Quadrant4Rows.Count;

    public bool IsTodoView => ViewMode == TodoViewMode.Todo;

    public bool IsDoneView => ViewMode == TodoViewMode.Done;

    public bool HasNoRows => Rows.Count == 0;

    /// <summary>「待办 3」这类标签，带未完成数量。</summary>
    [ObservableProperty]
    private string _todoViewText = Loc.T("view.todo");

    [ObservableProperty]
    private string _doneViewText = Loc.T("view.done");

    public string EmptyHint => TodoViewFilter.EmptyHint(ViewMode);

    /// <summary>草稿行是否可用（只有待办视图 + 未锁定时能新增）。</summary>
    public bool CanAdd => IsTodoView && !IsLocked;

    public void SetLocked(bool locked)
    {
        IsLocked = locked;
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(DraftPlaceholder));
    }

    public void SetTopmost(bool topmost) => IsTopmost = topmost;

    public string DraftPlaceholder => CanAdd
        ? Loc.T("board.draftPlaceholder")
        : IsDoneView
            ? Loc.T("board.doneViewReadOnly")
            : Loc.T("board.locked");

    /// <summary>提交某一行改过的标题。返回 false 表示没改成（空标题等）。</summary>
    public async Task<bool> CommitTitleAsync(TodoRowViewModel row)
    {
        // 无论标题有没有变化，离开编辑态时都要复位标记
        row.IsEditing = false;

        var newTitle = row.Title?.Trim() ?? string.Empty;
        if (string.Equals(newTitle, row.Item.Title, StringComparison.Ordinal))
        {
            row.Title = row.Item.Title;
            row.EditError = null;
            return true;
        }

        row.EditError = null;

        if (string.IsNullOrEmpty(newTitle))
        {
            row.EditError = Loc.T("row.titleEmpty");
            row.Title = row.Item.Title;
            return false;
        }

        try
        {
            ErrorText = null;
            await Task.Run(() => _service.UpdateTitle(row.Id, newTitle));

            // 原地更新，避免整表重建导致焦点丢失
            row.Item.Title = newTitle;
            row.Title = newTitle;
            var index = _all.FindIndex(t => t.Id == row.Id);
            if (index >= 0)
            {
                _all[index].Title = newTitle;
            }

            return true;
        }
        catch (Exception ex)
        {
            row.EditError = ex.Message;
            row.Title = row.Item.Title;
            return false;
        }
    }

    /// <summary>提交末尾的草稿行：有内容就新增一条，然后清空草稿继续等下一次输入。</summary>
    public async Task<bool> CommitDraftAsync()
    {
        var title = DraftTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        if (!CanAdd)
        {
            ErrorText = DraftPlaceholder;
            DraftTitle = string.Empty;
            return false;
        }

        try
        {
            ErrorText = null;
            // 当前选中的是一个具体标签时，新增的待办直接归到这个标签下
            var chip = SelectedChip;

            var created = await Task.Run(() =>
            {
                var todo = _service.AddTodo(BoardId, title);

                if (chip.IsRealTag && chip.TagId is { } tagId)
                {
                    _service.SetTodoTags(todo.Id, [tagId]);
                }

                return todo;
            });
            DraftTitle = string.Empty;
            await LoadAsync();

            // 新增后把光标留在草稿行，方便连续录入
            return created is not null;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return false;
        }
    }

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

    /// <summary>本看板自己的整窗透明度（没单独设过就跟随全局）。</summary>
    public double CurrentWindowOpacity => _settings.GetWindowOpacity(AppearanceKey);

    /// <summary>外观设置存在哪个 key 下：看板用它自己的 id。</summary>
    public string AppearanceKey => BoardId;

    /// <summary>这个看板有没有单独设过透明度（false = 跟随全局）。</summary>
    public bool HasOwnWindowOpacity => _settings.HasWindowOpacity(AppearanceKey);

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
        // 只改这个看板：存到 board.<id> 下，其它窗口不受影响
        _settings.SetWindowOpacity(AppearanceKey, opacity);
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
        OnPropertyChanged(nameof(CurrentWindowOpacity));
    }

    /// <summary>取消本看板的单独设置，回到「跟随全局」。</summary>
    public void ResetWindowOpacity()
    {
        _settings.ClearWindowOpacity(AppearanceKey);
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
        OnPropertyChanged(nameof(CurrentWindowOpacity));
    }

    /// <summary>皮肤被别的窗口改过之后刷新一下绑定值。</summary>
    public void RefreshAppearance()
    {
        OnPropertyChanged(nameof(CurrentSkinKey));
        OnPropertyChanged(nameof(CurrentOpacity));
        OnPropertyChanged(nameof(CurrentWindowOpacity));
        WindowOpacity = _settings.GetWindowOpacity(AppearanceKey);
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

    // ---------- 重复规则 ----------

    public async Task SetRecurrenceAsync(TodoRowViewModel row, string? rrule)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.SetRecurrence(row.Id, rrule));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

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

    /// <summary>
    /// 语言切换后重建所有由翻译器拼出来的文案。
    /// Split 视图文本、状态行、标签胶囊名（「全部」「未分类」）都是运行时拼的，
    /// 光靠 XAML 绑定的刷新不够，必须主动重算。
    /// </summary>
    public void RefreshLocalizedText()
    {
        foreach (var chip in Chips)
        {
            if (chip.TagId is null)
            {
                chip.SetName(Loc.T("tag.all"));
            }
            else if (chip.TagId == TagChipViewModel.NoTagId)
            {
                chip.SetName(Loc.T("tag.none"));
            }
        }

        SyncRows();

        // 「已删除…」提示也是拼出来的文案
        if (_recentlyDeleted is { } pending)
        {
            UndoDeleteText = Loc.F("row.deleted", pending.Title);
        }

        OnPropertyChanged(nameof(DraftPlaceholder));

        if (Rows.Count == 0)
        {
            StatusText = Loc.F("board.itemCount", 0);
        }
    }

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
        SyncRows();
    }

    private void RebuildChips()
    {
        var selectedBefore = Chips.FirstOrDefault(c => c.IsSelected);
        var previous = selectedBefore?.TagId;
        Chips.Clear();

        var all = new TagChipViewModel(null, Loc.T("tag.all"), TagColors.Default) { Count = _all.Count };
        Chips.Add(all);
        var noTag = new TagChipViewModel(TagChipViewModel.NoTagId, Loc.T("tag.none"), TagColors.Default)
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

        // 首次构建时没有「上一个选中」，就恢复上次用的胶囊
        var wanted = selectedBefore is not null
            ? ChipKeyOf(selectedBefore)
            : _settings.GetBoardChip(BoardId);

        var target = wanted switch
        {
            null or "all" => all,
            "none" => noTag,
            _ => Chips.FirstOrDefault(c => c.TagId == wanted) ?? all,
        };

        target.IsSelected = true;
    }

    /// <summary>胶囊的持久化标识：全部 / 未分类 / 标签 id。</summary>
    private static string ChipKeyOf(TagChipViewModel chip) =>
        chip.TagId is null ? "all" : chip.TagId.Length == 0 ? "none" : chip.TagId;

    private TagChipViewModel SelectedChip => Chips.FirstOrDefault(c => c.IsSelected) ?? Chips[0];

    /// <summary>给回归用：已完成总数（全量，不做标签/视图过滤）。</summary>
    internal int CompletedCountForTest => _all.Count(t => t.IsDone);

    /// <summary>
    /// 把行列表同步成"当前筛选 + 排序"应有的样子。
    ///
    /// 以前这里是 <c>Rows.Clear()</c> + 整表重建：每勾一条、删一条都要新建 N 个行对象
    /// 并把列表容器全部重造，滚动位置、悬停态、正在编辑的行全被打断。
    /// 现在按 id 复用已有行（原地 <see cref="TodoRowViewModel.Refresh"/>），
    /// 只对真正增/删/换位的行动集合。
    /// </summary>
    private void SyncRows()
    {
        var chip = SelectedChip;

        // 标签筛选 & 视图（待办/已完成）是两层独立条件
        var scoped = _all.Where(Matches).ToList();
        var visible = TodoViewFilter.SortForView(
            scoped.Where(t => TodoViewFilter.Matches(t.IsDone, ViewMode)),
            ViewMode,
            t => t.Sort,
            t => t.DoneAt).ToList();

        var now = DateTimeOffset.Now;

        // 已有行按 id 建索引；下面按目标顺序逐个复用，剩下的就是该删除的
        var existing = new Dictionary<string, TodoRowViewModel>(Rows.Count, StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            existing[row.Id] = row;
        }

        var desired = new List<TodoRowViewModel>(visible.Count);
        foreach (var item in visible)
        {
            if (existing.Remove(item.Id, out var row))
            {
                row.Refresh(item, TagTextOf(item.Id), now);
            }
            else
            {
                row = new TodoRowViewModel(item, TagTextOf(item.Id), now);
            }

            desired.Add(row);
        }

        // 不再可见的行（被过滤掉 / 删除了）移出集合
        if (existing.Count > 0)
        {
            foreach (var gone in existing.Values)
            {
                Rows.Remove(gone);
            }
        }

        // 剩下的差集就是顺序变化：就地 Move，新行 Insert
        for (var i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            var index = Rows.IndexOf(row);
            if (index == i)
            {
                continue;
            }

            if (index >= 0)
            {
                Rows.Move(index, i);
            }
            else
            {
                Rows.Insert(i, row);
            }
        }

        OnPropertyChanged(nameof(HasNoRows));

        var undone = scoped.Count(t => !t.IsDone);
        var done = scoped.Count - undone;
        TodoViewText = undone > 0 ? Loc.F("view.todoCount", undone) : Loc.T("view.todo");
        DoneViewText = done > 0 ? Loc.F("view.doneCount", done) : Loc.T("view.done");

        var filterName = chip.IsAll ? string.Empty : chip.Name;
        StatusText = HasSearch
            ? Loc.F("board.searchCount", visible.Count, SearchText.Trim())
            : string.IsNullOrEmpty(filterName)
                ? Loc.F("board.itemCount", visible.Count)
                : Loc.F("board.itemCountInTag", visible.Count, filterName);

        RebuildQuadrants();

        return;

        // 标签胶囊 & 搜索关键字是「与」的关系：两个都满足才可见
        bool Matches(TodoItem item)
        {
            var tagOk = chip.TagId switch
            {
                null => true,
                TagChipViewModel.NoTagId => !_tagMap.TryGetValue(item.Id, out var ids) || ids.Count == 0,
                _ => _tagMap.TryGetValue(item.Id, out var ids) && ids.Contains(chip.TagId),
            };

            return tagOk && MatchesSearch(item);
        }
    }

    /// <summary>
    /// 把当前可见行按象限分组（复用同一批行对象）。
    /// 没有任何象限标记的行按「② 重要不紧急」显示，只是显示层默认，不改库。
    /// </summary>
    private void RebuildQuadrants()
    {
        Quadrant1Rows.Clear();
        Quadrant2Rows.Clear();
        Quadrant3Rows.Clear();
        Quadrant4Rows.Clear();

        foreach (var row in Rows)
        {
            var target = EffectiveQuadrant(row.Item) switch
            {
                0 => Quadrant1Rows,
                2 => Quadrant3Rows,
                3 => Quadrant4Rows,
                _ => Quadrant2Rows,
            };

            target.Add(row);
        }

        OnPropertyChanged(nameof(Quadrant1Count));
        OnPropertyChanged(nameof(Quadrant2Count));
        OnPropertyChanged(nameof(Quadrant3Count));
        OnPropertyChanged(nameof(Quadrant4Count));
    }

    /// <summary>四象限归属：0=① 1=② 2=③ 3=④；未归类按 ② 显示。</summary>
    public static int EffectiveQuadrant(TodoItem item) => item.Quadrant is >= 0 and <= 3 ? item.Quadrant.Value : 1;

    /// <summary>拖动改象限。</summary>
    public async Task SetQuadrantAsync(TodoRowViewModel row, int quadrant)
    {
        if (EffectiveQuadrant(row.Item) == quadrant && row.Item.Quadrant is not null)
        {
            return;
        }

        try
        {
            ErrorText = null;
            await Task.Run(() => _service.SetQuadrant(row.Id, quadrant));
            row.Item.Quadrant = quadrant;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>在某一格里直接新增一条（自动带上该象限标记）。</summary>
    public async Task<bool> AddToQuadrantAsync(int quadrant, string title)
    {
        var clean = title?.Trim() ?? string.Empty;
        if (clean.Length == 0)
        {
            return false;
        }

        try
        {
            ErrorText = null;

            await Task.Run(() =>
            {
                var todo = _service.AddTodo(BoardId, clean);
                _service.SetQuadrant(todo.Id, quadrant);
            });

            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return false;
        }
    }

    /// <summary>列表 / 四象限 切换（记住选择）。</summary>
    public void ToggleQuadrantView()
    {
        IsQuadrantView = !IsQuadrantView;

        try
        {
            _settings.SetBoardView(BoardId, IsQuadrantView ? "quadrant" : "list");
        }
        catch (Exception)
        {
            // 记不住不影响使用
        }
    }

    /// <summary>
    /// 关键字匹配：标题 / 标签名 / 倒数日文案（「今天到期」「还剩 3 天」）都能搜到，
    /// 这样输入「今天」也能找出今天到期的那些。
    /// </summary>
    private bool MatchesSearch(TodoItem item)
    {
        var keyword = SearchText.Trim();
        if (keyword.Length == 0)
        {
            return true;
        }

        if (item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TagTextOf(item.Id).Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var due = DueDateText.Describe(item.DueAt, DateTimeOffset.Now);
        return due.Length > 0 && due.Contains(keyword, StringComparison.OrdinalIgnoreCase);
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

    partial void OnViewModeChanged(TodoViewMode value)
    {
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(DraftPlaceholder));
        SyncRows();
    }

    /// <summary>切换视图。</summary>
    public void SwitchView(TodoViewMode mode) => ViewMode = mode;

    public async Task SelectChipAsync(TagChipViewModel chip)
    {
        foreach (var item in Chips)
        {
            item.IsSelected = ReferenceEquals(item, chip);
        }

        // 记住这次选择：下次打开这个看板直接用同一个筛选
        try
        {
            _settings.SetBoardChip(BoardId, ChipKeyOf(chip));
        }
        catch (Exception)
        {
            // 记不住不影响使用
        }

        SyncRows();
        await Task.CompletedTask;
    }

    /// <summary>✓ 标记完成。重复待办走「滚到下一次」，普通待办直接置为已完成。</summary>
    public async Task MarkDoneAsync(TodoRowViewModel row)
    {
        try
        {
            ErrorText = null;
            var isRecurring = !string.IsNullOrWhiteSpace(row.Item.Recurrence);

            await Task.Run(() =>
            {
                if (isRecurring)
                {
                    _service.CompleteOccurrence(row.Id);
                }
                else
                {
                    _service.SetDone(row.Id, true);
                }
            });

            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>↺ 撤回为待办。</summary>
    public async Task MarkUndoneAsync(TodoRowViewModel row)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.SetDone(row.Id, false));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>✕ 删除（行悬停按钮用）。删完底部给一条可撤销的提示。</summary>
    public Task DeleteRowAsync(TodoRowViewModel row) => DeleteWithUndoAsync(row);

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
            var isRecurring = !string.IsNullOrWhiteSpace(row.Item.Recurrence);

            await Task.Run(() =>
            {
                if (isRecurring && !row.IsDone)
                {
                    // 重复待办：勾选 = 记一次完成 + 目标日期滚到下一次（不消失）
                    _service.CompleteOccurrence(row.Id);
                }
                else
                {
                    _service.SetDone(row.Id, !row.IsDone);
                }
            });

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

        await DeleteWithUndoAsync(row);
    }

    // ---------------- 删除可撤销 ----------------

    /// <summary>刚删掉的那条，够用来原样加回来。</summary>
    private sealed record DeletedTodo(
        string Title,
        DateTimeOffset? DueAt,
        DateTimeOffset? RemindAt,
        string? Recurrence,
        bool WasDone,
        IReadOnlyList<string> TagIds,
        string? NextId);

    private DeletedTodo? _recentlyDeleted;

    /// <summary>撤销窗口（8 秒）。过了就把它定型成真删除，不再显示提示条。</summary>
    private readonly DispatcherTimer _undoTimer;

    /// <summary>底部「已删除 … · 撤销」提示条是否可见。</summary>
    [ObservableProperty]
    private bool _hasUndoDelete;

    /// <summary>提示条上的文案，如「已删除「买牛奶」」。</summary>
    [ObservableProperty]
    private string _undoDeleteText = string.Empty;

    /// <summary>删除一条待办：数据库立刻删，但底部留 8 秒「撤销」——误点 ✕ 也能救回来。</summary>
    private async Task DeleteWithUndoAsync(TodoRowViewModel row)
    {
        try
        {
            ErrorText = null;

            var snapshot = new DeletedTodo(
                row.Item.Title,
                row.Item.DueAt,
                row.Item.RemindAt,
                row.Item.Recurrence,
                row.IsDone,
                TagsOf(row.Id),
                NextVisibleIdAfter(row));

            await Task.Run(() => _service.Delete(row.Id));
            await LoadAsync();

            _recentlyDeleted = snapshot;
            UndoDeleteText = Loc.F("row.deleted", snapshot.Title);
            HasUndoDelete = true;
            _undoTimer.Stop();
            _undoTimer.Start();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>撤销上一条删除：按原样加回来（标题 / 目标日期 / 提醒 / 重复 / 完成状态 / 标签 / 原位置）。</summary>
    public async Task UndoDeleteAsync()
    {
        var snapshot = _recentlyDeleted;
        ClearUndoDelete();

        if (snapshot is null)
        {
            return;
        }

        try
        {
            ErrorText = null;
            await Task.Run(() =>
            {
                var restored = _service.AddTodo(BoardId, snapshot.Title, snapshot.DueAt);

                if (snapshot.TagIds.Count > 0)
                {
                    _service.SetTodoTags(restored.Id, snapshot.TagIds);
                }

                if (snapshot.RemindAt is { } remind)
                {
                    _service.SetRemindAt(restored.Id, remind);
                }

                if (!string.IsNullOrWhiteSpace(snapshot.Recurrence))
                {
                    _service.SetRecurrence(restored.Id, snapshot.Recurrence);
                }

                if (snapshot.WasDone)
                {
                    _service.SetDone(restored.Id, true);
                }

                // 回到它原来在列表里的位置（删的是最后一条就落回末尾）
                if (snapshot.NextId is { } next)
                {
                    _service.MoveBefore(BoardId, restored.Id, next);
                }
            });

            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private void ClearUndoDelete()
    {
        _undoTimer.Stop();
        _recentlyDeleted = null;
        HasUndoDelete = false;
        UndoDeleteText = string.Empty;
    }

    /// <summary>这条待办身上的标签 id（撤销时按原样挂回去）。</summary>
    private IReadOnlyList<string> TagsOf(string todoId) =>
        _tagMap.TryGetValue(todoId, out var ids) ? ids.ToList() : [];

    /// <summary>它下面那一行（撤销时插回原位）；它本来就是最后一行就返回 null = 放末尾。</summary>
    private string? NextVisibleIdAfter(TodoRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        return index >= 0 && index + 1 < Rows.Count ? Rows[index + 1].Id : null;
    }

    /// <summary>拖拽落点：把 movingId 放到 targetId 之前，null 表示末尾。</summary>
    public async Task MoveBeforeAsync(string movingId, string? targetId)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.MoveBefore(BoardId, movingId, targetId));
            await LoadAsync();
            Log.Debug("[drag] 拖拽完成：" + string.Join(",", Rows.Select(r => r.Title)));
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

    /// <summary>改标签颜色（右键菜单里的色板）。</summary>
    public async Task SetTagColorAsync(Tag tag, string color)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.SetTagColor(tag.Id, color));
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

    /// <summary>拖到标签胶囊上：给这条待办加上该标签（已有则不重复）。</summary>
    public async Task AddTagAsync(TodoRowViewModel row, string tagId)
    {
        try
        {
            ErrorText = null;
            var ids = _tagMap.TryGetValue(row.Id, out var current) ? current.ToList() : [];

            if (ids.Contains(tagId))
            {
                return;
            }

            ids.Add(tagId);
            await Task.Run(() => _service.SetTodoTags(row.Id, ids));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("[tag] AddTagAsync 失败：" + ex.Message);
            ErrorText = ex.Message;
        }
    }

    /// <summary>拖到「全部」或「未分类」上：清空这条待办的所有标签。</summary>
    public async Task ClearTagsAsync(TodoRowViewModel row)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.SetTodoTags(row.Id, Array.Empty<string>()));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }
}
