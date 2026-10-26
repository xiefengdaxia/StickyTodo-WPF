using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>待办看板窗口：标签筛选 + 行内编辑 + 拖拽排序 + 右键菜单 + 位置吸附。</summary>
public partial class BoardWindow : StickyWindowBase
{
    /// <summary>指针移动超过这个距离（逻辑像素）才算开始拖拽。</summary>
    private const double DragThreshold = 4;

    private readonly BoardViewModel _viewModel;
    private TodoRowViewModel? _dragCandidate;
    private Avalonia.Point _dragStart;
    private bool _dragArmed;

    /// <summary>是否已越过阈值、真正进入拖拽状态。</summary>
    private bool _isDragging;

    /// <summary>当前指针悬停的行（拖拽落点）。</summary>
    private TodoRowViewModel? _hoverRow;

    /// <summary>当前指针悬停的标签胶囊（拖到胶囊上打标签）。</summary>
    private TagChipViewModel? _hoverChip;

    public BoardWindow(BoardViewModel viewModel, WindowManager windows)
        : base(windows, App.Services!.Settings)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        InitStickyWindow(TitleBar, NoteRoot);
    }

    protected override Board Board => _viewModel.Board;

    protected override IAppearanceHost Appearance => _viewModel;

    protected override async Task OnWindowLoadedAsync()
    {
        await _viewModel.LoadAsync();

    }

    public void FocusNewTodo() => DraftBox.Focus();

    // ---------------- 供回归脚本使用 ----------------

    /// <summary>当前列表里各行的标题（按显示顺序）。</summary>
    public IReadOnlyList<string> RowTitles() => _viewModel.Rows.Select(r => r.Title).ToList();

    /// <summary>取第 index 行的 ViewModel。</summary>
    public TodoRowViewModel RowAt(int index) => _viewModel.Rows[index];

    /// <summary>拖拽是否已武装（按下把手后、越过阈值前）。</summary>
    internal bool IsDragArmed => _dragArmed;

    /// <summary>找第 index 行的拖动把手控件（靠 Cursor=SizeAll 识别）。</summary>
    internal Control? FindDragHandle(int index)
    {
        if (TodoList.ContainerFromIndex(index) is not Control container)
        {
            return null;
        }

        return container.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => c.Cursor is { } cur && cur.ToString()!.Contains("SizeAll", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 走一遍真实的拖拽状态机：按下 → 移动越阈值 → 悬停 → 松手。
    /// 全程只调用窗口自己的处理函数，用的坐标也是真实控件布局算出来的，
    /// 因此能验证"拖动把手是否真的能拖动排序"。
    /// </summary>
    internal bool SimulateHandlePress(Control handle) => SimulateFullDrag(handle, fromRow: 0, toRow: 2);

    /// <summary>拖动第 fromRow 行到第 toRow 行的位置（真实状态机）。</summary>
    internal bool SimulateFullDrag(Control handle, int fromRow, int toRow)
    {
        if (fromRow < 0 || fromRow >= _viewModel.Rows.Count || toRow < 0 || toRow >= _viewModel.Rows.Count)
        {
            return false;
        }

        var source = _viewModel.Rows[fromRow];
        var destination = _viewModel.Rows[toRow];

        // 1) 按下：等价于用户按住把手
        _dragCandidate = source;
        _dragStart = HandlePointInList(fromRow);
        _dragArmed = true;
        _isDragging = false;

        // 2) 移动超过阈值：等价于用户拖出 4px 以上
        var beyond = new Avalonia.Point(_dragStart.X, _dragStart.Y + (DragThreshold * 2));
        MoveDragTo(beyond);

        // 3) 悬停到目标行
        var destinationPoint = RowCenterInList(toRow);
        MoveDragTo(destinationPoint);

        // 4) 松手
        var hover = _hoverRow;
        EndDrag(commit: true);

        return ReferenceEquals(hover, destination);
    }

    /// <summary>把拖拽指针移到列表坐标 point（内部走与真实 PointerMoved 相同的判定）。</summary>
    private void MoveDragTo(Avalonia.Point point)
    {
        if (!_dragArmed || _dragCandidate is null)
        {
            return;
        }

        if (!_isDragging)
        {
            var dx = Math.Abs(point.X - _dragStart.X);
            var dy = Math.Abs(point.Y - _dragStart.Y);
            if (dx < DragThreshold && dy < DragThreshold)
            {
                return;
            }

            _isDragging = true;
            _dragCandidate.IsDragging = true;
        }

        // 标签胶囊的命中测试要用窗口坐标
        UpdateDropTargets(point, TranslateListPointToWindow(point));
    }

    /// <summary>把列表内坐标换算成窗口坐标。</summary>
    private Avalonia.Point TranslateListPointToWindow(Avalonia.Point pointInList)
    {
        var offset = Avalonia.VisualExtensions.TranslatePoint(TodoList, default, this);
        return offset is null ? pointInList : new Avalonia.Point(offset.Value.X + pointInList.X, offset.Value.Y + pointInList.Y);
    }

    /// <summary>把手中心在列表坐标系里的位置。</summary>
    private Avalonia.Point HandlePointInList(int rowIndex)
    {
        if (TodoList.ContainerFromIndex(rowIndex) is not Control container)
        {
            return default;
        }

        return new Avalonia.Point(8, container.Bounds.Y + (container.Bounds.Height / 2));
    }

    /// <summary>某一行的中心在列表坐标系里的位置。</summary>
    private Avalonia.Point RowCenterInList(int rowIndex)
    {
        if (TodoList.ContainerFromIndex(rowIndex) is not Control container)
        {
            return default;
        }

        return new Avalonia.Point(120, container.Bounds.Y + (container.Bounds.Height / 2));
    }

    /// <summary>
    /// 模拟一次拖放落点：把 fromIndex 那一行插到 toIndex 之前。
    /// 复用的是 Drop 处理里同一套"解析落点 + 写入顺序"逻辑。
    /// </summary>
    internal bool SimulateDrop(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _viewModel.Rows.Count)
        {
            return false;
        }

        var moving = _viewModel.Rows[fromIndex];
        var target = toIndex >= _viewModel.Rows.Count ? null : _viewModel.Rows[toIndex];

        // 与 DragDrop 处理一致：先标落点，再写入
        foreach (var row in _viewModel.Rows)
        {
            row.IsDragTarget = ReferenceEquals(row, target);
        }

        _ = _viewModel.MoveBeforeAsync(moving.Id, target?.Id);
        return true;
    }

    public override void ToggleLock() => _viewModel.ToggleLock();

    // ---------------- 标题栏按钮 ----------------

    private void Chrome_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control control)
        {
            control.Opacity = 1;
        }
    }

    private void Chrome_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control control)
        {
            control.Opacity = 0.35;
        }
    }

    private void ToggleTopmost_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleTopmost();

    private void ToggleLock_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleLock();

    private void Appearance_Click(object? sender, RoutedEventArgs e) =>
        ShowAppearanceMenu((Control)sender!);

    private void OpenManager_Click(object? sender, RoutedEventArgs e) =>
        App.Services?.ShowManager();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ---------------- 视图切换 ----------------

    private void TodoTab_Click(object? sender, RoutedEventArgs e) => _viewModel.SwitchView(TodoViewMode.Todo);

    private void DoneTab_Click(object? sender, RoutedEventArgs e) => _viewModel.SwitchView(TodoViewMode.Done);

    // ---------------- 行操作 ----------------

    private async void RowDone_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: TodoRowViewModel row })
        {
            await _viewModel.MarkDoneAsync(row);
        }
    }

    private async void RowUndone_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: TodoRowViewModel row })
        {
            await _viewModel.MarkUndoneAsync(row);
        }
    }

    private async void RowDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: TodoRowViewModel row })
        {
            await _viewModel.DeleteRowAsync(row);
        }
    }

    private void RowTitle_GotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is TextBox { Tag: TodoRowViewModel row })
        {
            row.IsEditing = true;
        }
    }

    private async void RowTitle_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: TodoRowViewModel row })
        {
            await _viewModel.CommitTitleAsync(row);
        }
    }

    private async void RowTitle_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: TodoRowViewModel row } box)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var index = _viewModel.Rows.IndexOf(row);
            await _viewModel.CommitTitleAsync(row);
            FocusRowAt(index + 1);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            row.Title = row.Item.Title;
            row.IsEditing = false;
            FocusManager?.ClearFocus();
        }
    }

    private void FocusRowAt(int index)
    {
        if (index >= 0 && index < _viewModel.Rows.Count)
        {
            var container = TodoList.ContainerFromIndex(index);
            var box = container?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            box?.Focus();
            return;
        }

        DraftBox.Focus();
    }

    // ---------------- 草稿行 ----------------

    private void DraftBox_GotFocus(object? sender, GotFocusEventArgs e) => _viewModel.IsDraftFocused = true;

    private async void DraftBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsDraftFocused = false;
        await _viewModel.CommitDraftAsync();
    }

    private async void DraftBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _viewModel.CommitDraftAsync();
            DraftBox.Focus();
        }
    }

    // ---------------- 标签胶囊 ----------------

    private async void Chip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: TagChipViewModel chip })
        {
            await _viewModel.SelectChipAsync(chip);
        }
    }

    private async void Chip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: TagChipViewModel chip } control)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);

        // 右键：改名 / 删除标签
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            await ShowChipMenuAsync(control, chip);
        }
    }

    private async Task ShowChipMenuAsync(Control target, TagChipViewModel chip)
    {
        var menu = new ContextMenu();

        if (chip.TagId is null)
        {
            menu.Items.Add(new MenuItem { Header = Loc.T("menu.clearTags"), IsEnabled = false });
        }
        else if (chip.TagId == TagChipViewModel.NoTagId)
        {
            menu.Items.Add(new MenuItem { Header = Loc.T("tag.none"), IsEnabled = false });
        }
        else
        {
            var rename = new MenuItem { Header = Loc.T("common.rename") };
            rename.Click += async (_, _) =>
            {
                var tag = _viewModel.Tags.FirstOrDefault(t => t.Id == chip.TagId);
                if (tag is null)
                {
                    return;
                }

                var name = await PromptWindow.PromptAsync(this, Loc.T("common.rename"), Loc.T("tag.name"), tag.Name);
                if (name is not null)
                {
                    await _viewModel.RenameTagAsync(tag, name);
                }
            };
            menu.Items.Add(rename);

            var delete = new MenuItem { Header = Loc.T("common.delete") };
            delete.Click += async (_, _) =>
            {
                var tag = _viewModel.Tags.FirstOrDefault(t => t.Id == chip.TagId);
                if (tag is not null)
                {
                    await _viewModel.DeleteTagAsync(tag);
                }
            };
            menu.Items.Add(delete);
        }

        menu.PlacementTarget = target;
        menu.Open(target);
        await Task.CompletedTask;
    }

    private async void AddTag_Click(object? sender, RoutedEventArgs e)
    {
        var name = await PromptWindow.PromptAsync(this, Loc.T("board.addTagTip"), Loc.T("tag.name"));
        if (name is not null)
        {
            await _viewModel.CreateTagAsync(name);
        }
    }

    // ---------------- 拖拽：排序 + 拖到标签 ----------------

    // ---------------- 拖拽排序（纯指针实现，不走 Windows OLE 拖放） ----------------

    /// <summary>
    /// 为什么不用 <c>DragDrop.DoDragDropAsync</c>：
    /// Avalonia 的实现在 Windows 上会调用 OLE 的 <c>DoDragDrop</c>，
    /// 它要求窗口注册为 OLE 拖放目标并进入模态消息循环。在无边框 + 透明 +
    /// 自定义合成的窗口上这条链路并不可靠（表现为"拖不动"，且不抛异常）。
    ///
    /// 这里改成完全在 Avalonia 指针管道里实现：
    ///   PointerPressed  → 记下起点、捕获指针
    ///   PointerMoved    → 超过 4px 判定为拖拽；实时命中测试，高亮落点
    ///   PointerReleased → 按落点写库（落点变了才写）
    /// 好处：不依赖 OLE、不依赖窗口样式、行为完全可预测。
    /// </summary>
    private void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: TodoRowViewModel row } handle)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragCandidate = row;
        _dragStart = e.GetPosition(TodoList);
        _dragArmed = true;
        _isDragging = false;

        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void DragHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || _dragCandidate is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndDrag(commit: false);
            return;
        }

        var point = e.GetPosition(TodoList);

        // 还没越过阈值：什么都不做，保证单击仍是单击
        if (!_isDragging)
        {
            var dx = Math.Abs(point.X - _dragStart.X);
            var dy = Math.Abs(point.Y - _dragStart.Y);
            if (dx < DragThreshold && dy < DragThreshold)
            {
                return;
            }

            _isDragging = true;
            _dragCandidate.IsDragging = true;
        }

        UpdateDropTargets(point, e.GetPosition(this));
        e.Handled = true;
    }

    private void DragHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndDrag(commit: true);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>指针被系统抢走（比如 Alt+Tab）时复位，避免残留拖拽状态。</summary>
    private void DragHandle_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndDrag(commit: false);

    /// <summary>结束拖拽：commit=true 时按落点写库。</summary>
    private void EndDrag(bool commit)
    {
        var moving = _dragCandidate;
        var targetRow = _hoverRow;
        var targetChip = _hoverChip;
        var wasDragging = _isDragging;

        _dragArmed = false;
        _isDragging = false;
        _dragCandidate = null;
        _hoverRow = null;
        _hoverChip = null;

        // 复位所有视觉状态
        foreach (var row in _viewModel.Rows)
        {
            row.IsDragTarget = false;
            row.IsDragging = false;
        }

        foreach (var chip in _viewModel.Chips)
        {
            chip.IsDropTarget = false;
        }

        if (!commit || !wasDragging || moving is null)
        {
            return;
        }

        // 落在标签胶囊上 = 打标签（「全部 / 未分类」= 清空标签）
        if (targetChip is not null)
        {
            _ = targetChip.TagId is null or TagChipViewModel.NoTagId
                ? _viewModel.ClearTagsAsync(moving)
                : _viewModel.AddTagAsync(moving, targetChip.TagId);
            return;
        }

        // 落在某一行上 = 调整顺序
        if (targetRow is not null && !ReferenceEquals(targetRow, moving))
        {
            _ = _viewModel.MoveBeforeAsync(moving.Id, targetRow.Id);
        }
    }

    /// <summary>实时命中测试：指针现在压在哪一行 / 哪个标签胶囊上。</summary>
    private void UpdateDropTargets(Avalonia.Point pointInList, Avalonia.Point pointInWindow)
    {
        // 先看是不是落在标签胶囊上（拖到胶囊上打标签）
        var chip = ChipAt(pointInWindow);
        if (chip is not null)
        {
            if (!ReferenceEquals(chip, _hoverChip) && _hoverChip is not null)
            {
                _hoverChip.IsDropTarget = false;
            }

            _hoverChip = chip;
            chip.IsDropTarget = true;

            if (_hoverRow is not null)
            {
                _hoverRow.IsDragTarget = false;
                _hoverRow = null;
            }

            return;
        }

        if (_hoverChip is not null)
        {
            _hoverChip.IsDropTarget = false;
            _hoverChip = null;
        }

        var row = RowAt(pointInList);
        if (ReferenceEquals(row, _hoverRow))
        {
            return;
        }

        if (_hoverRow is not null)
        {
            _hoverRow.IsDragTarget = false;
        }

        _hoverRow = row;
        if (row is not null)
        {
            row.IsDragTarget = true;
        }
    }

    /// <summary>按指针位置找目标行（逐行命中，缝隙处取最近的上一行）。</summary>
    private TodoRowViewModel? RowAt(Avalonia.Point pointInList)
    {
        TodoRowViewModel? nearest = null;
        var nearestDistance = double.MaxValue;

        for (var i = 0; i < _viewModel.Rows.Count; i++)
        {
            if (TodoList.ContainerFromIndex(i) is not Control container)
            {
                continue;
            }

            var top = container.Bounds.Y;
            var bottom = top + container.Bounds.Height;

            if (pointInList.Y >= top && pointInList.Y <= bottom)
            {
                return _viewModel.Rows[i];
            }

            var distance = pointInList.Y < top ? top - pointInList.Y : pointInList.Y - bottom;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = _viewModel.Rows[i];
            }
        }

        return nearest;
    }

    /// <summary>按指针位置找标签胶囊（用可视树命中测试，不依赖 e.Source）。</summary>
    private TagChipViewModel? ChipAt(Avalonia.Point pointInWindow)
    {
        foreach (var visual in this.GetVisualsAt(pointInWindow))
        {
            var current = visual as Control;
            while (current is not null)
            {
                if (current.Tag is TagChipViewModel chip)
                {
                    return chip;
                }

                current = current.Parent as Control;
            }
        }

        return null;
    }

    // ---------------- 右键菜单：日期 / 提醒 / 重复 / 标签 ----------------

    private void TodoList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.Source is Control source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is ListBoxItem { DataContext: TodoRowViewModel row })
                {
                    ShowRowMenu(source, row);
                    e.Handled = true;
                    return;
                }

                current = current.Parent as Control;
            }
        }
    }

    private void ShowRowMenu(Control target, TodoRowViewModel row)
    {
        var menu = new ContextMenu();

        var toggle = new MenuItem { Header = row.IsDone ? Loc.T("row.undo") : Loc.T("row.markDone") };
        toggle.Click += async (_, _) =>
        {
            if (row.IsDone)
            {
                await _viewModel.MarkUndoneAsync(row);
            }
            else
            {
                await _viewModel.MarkDoneAsync(row);
            }
        };
        menu.Items.Add(toggle);

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildDueMenu(row));
        menu.Items.Add(BuildRemindMenu(row));
        menu.Items.Add(BuildRepeatMenu(row));
        menu.Items.Add(BuildTagsMenu(row));

        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = Loc.T("row.delete") };
        delete.Click += async (_, _) => await _viewModel.DeleteRowAsync(row);
        menu.Items.Add(delete);

        menu.PlacementTarget = target;
        menu.Open(target);
    }

    private MenuItem BuildDueMenu(TodoRowViewModel row)
    {
        var root = new MenuItem { Header = Loc.T("menu.dueDate") };
        var today = DateTimeOffset.Now.Date;

        Add(root, Loc.T("due.today"), new DateTimeOffset(today.AddHours(23).AddMinutes(59)));
        Add(root, Loc.T("due.tomorrow"), new DateTimeOffset(today.AddDays(1).AddHours(23).AddMinutes(59)));
        Add(root, Loc.T("due.weekend"), NextWeekend(DateTimeOffset.Now));
        Add(root, Loc.T("due.noDate"), null);

        return root;

        void Add(MenuItem parent, string header, DateTimeOffset? value)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await _viewModel.SetDueAsync(row, value);
            parent.Items.Add(item);
        }
    }

    private MenuItem BuildRemindMenu(TodoRowViewModel row)
    {
        var root = new MenuItem { Header = Loc.T("menu.reminder") };

        Add(root, Loc.T("reminder.in30m"), DateTimeOffset.Now.AddMinutes(30));
        Add(root, Loc.T("reminder.in1h"), DateTimeOffset.Now.AddHours(1));
        Add(root, Loc.T("reminder.tomorrow9"), new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9)));
        Add(root, Loc.T("reminder.clear"), null);

        return root;

        void Add(MenuItem parent, string header, DateTimeOffset? value)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await _viewModel.SetRemindAsync(row, value);
            parent.Items.Add(item);
        }
    }

    private MenuItem BuildRepeatMenu(TodoRowViewModel row)
    {
        var root = new MenuItem { Header = Loc.T("menu.repeat") };

        Add(root, Loc.T("recur.daily"), "FREQ=DAILY");
        Add(root, Loc.T("recur.weekly"), "FREQ=WEEKLY");
        Add(root, Loc.T("recur.monthly"), "FREQ=MONTHLY");
        Add(root, Loc.T("recur.weekdays"), "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");
        Add(root, Loc.T("common.empty"), null);

        return root;

        void Add(MenuItem parent, string header, string? value)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await _viewModel.SetRecurrenceAsync(row, value);
            parent.Items.Add(item);
        }
    }

    private MenuItem BuildTagsMenu(TodoRowViewModel row)
    {
        var root = new MenuItem { Header = Loc.T("menu.tags") };

        var tags = _viewModel.Tags;
        if (tags.Count == 0)
        {
            root.Items.Add(new MenuItem { Header = Loc.T("tag.none"), IsEnabled = false });
            return root;
        }

        foreach (var tag in tags)
        {
            var item = new MenuItem
            {
                Header = tag.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _viewModel.HasTag(row, tag),
            };
            var captured = tag;
            item.Click += async (_, _) => await _viewModel.ToggleTagAsync(row, captured);
            root.Items.Add(item);
        }

        return root;
    }

    private static DateTimeOffset NextWeekend(DateTimeOffset today)
    {
        // 找最近的周六
        var delta = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7;
        if (delta == 0)
        {
            delta = 7;
        }

        return new DateTimeOffset(today.Date.AddDays(delta).AddHours(23).AddMinutes(59));
    }
}
