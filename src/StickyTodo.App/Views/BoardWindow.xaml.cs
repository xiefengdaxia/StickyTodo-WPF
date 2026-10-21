using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Geometry;
using StickyTodo.Core.Recurrence;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Views;

/// <summary>
/// 一个看板一个窗口：位置/大小会被记住，拖到边缘或其它便签旁边会自动吸附。
/// </summary>
public partial class BoardWindow : Window, IStickyWindow
{
    /// <summary>便签外圈留给阴影的空白，吸附时要把它排除掉，否则纸面离屏幕边缘差 10px。</summary>
    private const double ShadowMargin = 10d;

    /// <summary>边缘拖拽缩放的热区宽度。</summary>
    private const double ResizeBorder = 8d;

    private const double SnapThreshold = 10d;
    private const int CascadeStep = 28;

    private const int WmNcHitTest = 0x0084;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private readonly BoardViewModel _viewModel;
    private readonly WindowManager _windows;
    private readonly DispatcherTimer _saveTimer;

    private bool _isSnapping;
    private bool _geometryRestored;
    private bool _isFullScreen;

    private Point _dragStart;
    private TodoRowViewModel? _dragCandidate;
    private bool _dragArmed;
    private TodoRowViewModel? _contextRow;
    public BoardWindow(BoardViewModel viewModel, WindowManager windows)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _windows = windows;
        DataContext = viewModel;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveGeometry();
        };

        Loaded += async (_, _) =>
        {
            RestoreGeometry();
            await _viewModel.LoadAsync();
            RefreshViewTabs();

            // 位置已经定好，再做入场（先定位后淡入，避免看到"从别处跳过来"）
            Motion.FadeInWindow(this);
        };

        // 点窗口里任何"非可编辑"的区域都要退出编辑态（含标签栏、空白区、标题栏）
        PreviewMouseLeftButtonDown += BoardWindow_PreviewMouseLeftButtonDown;

        StateChanged += (_, _) =>
        {
            _isFullScreen = WindowState == WindowState.Maximized;
            if (_isFullScreen)
            {
                // 最大化时不保留圆角和阴影，避免纸面被拉伸得很难看
                NoteRoot.CornerRadius = new CornerRadius(0);
                NoteRoot.Effect = null;
                NoteRoot.Margin = new Thickness(0);
            }
        };
    }

    /// <summary>
    /// 点到哪里算"离开编辑"：只要不是落在文本框、复选框、按钮上，就提交并清焦点。
    /// </summary>
    private async void BoardWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (IsEditableOrInteractive(source))
        {
            return;
        }

        await CommitEditingRowsAsync();
    }

    private static bool IsEditableOrInteractive(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            switch (current)
            {
                case System.Windows.Controls.Primitives.TextBoxBase:
                case System.Windows.Controls.Primitives.ToggleButton:
                case System.Windows.Controls.Primitives.ButtonBase:
                case System.Windows.Controls.Primitives.ScrollBar:
                    return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>提交所有正在编辑的行，并清掉焦点（草稿行也退出聚焦态）。</summary>
    private async Task CommitEditingRowsAsync()
    {
        var editing = _viewModel.Rows.Where(r => r.IsEditing).ToList();
        if (editing.Count == 0 && !_viewModel.IsDraftFocused)
        {
            return;
        }

        foreach (var row in editing)
        {
            await _viewModel.CommitTitleAsync(row);
        }

        _viewModel.IsDraftFocused = false;
        Keyboard.ClearFocus();
    }

    // ---------------- 标题栏拖动 / 关闭 ----------------

    /// <summary>切换视图时同时更新两个 Tab 的高亮（样式里配了 DataTrigger，这里只需刷新绑定）。</summary>
    private void RefreshViewTabs()
    {
        var todoActive = _viewModel.IsTodoView;
        TodoTab.Style = (Style)FindResource(todoActive ? "SegmentTab.Active" : "SegmentTab");
        DoneTab.Style = (Style)FindResource(todoActive ? "SegmentTab" : "SegmentTab.Active");
    }

    private void TodoTab_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SwitchView(TodoViewMode.Todo);
        RefreshViewTabs();
    }

    private void DoneTab_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SwitchView(TodoViewMode.Done);
        RefreshViewTabs();
    }

    // ---------------- 行悬停操作：✓ / ✕ / ↺ ----------------

    private async void RowDone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoRowViewModel row } button)
        {
            // 先给"这一行"一个即时反馈，再走数据（数据回来会把行从列表里移走）
            if (FindRowContainer(button) is { } container)
            {
                Motion.Pulse(container, 1.03);
            }

            await _viewModel.MarkDoneAsync(row);
        }
    }

    private async void RowUndone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoRowViewModel row } button)
        {
            if (FindRowContainer(button) is { } container)
            {
                Motion.Pulse(container, 1.03);
            }

            await _viewModel.MarkUndoneAsync(row);
        }
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoRowViewModel row } button)
        {
            var container = FindRowContainer(button);

            // 删除：先把这一行淡出，再真正删数据，避免"啪一下没了"
            if (container is not null && Motion.Enabled)
            {
                var completion = new TaskCompletionSource();
                Motion.FadeOutAndRemove(container, () => completion.TrySetResult());

                // 动画最多等 160ms，别让删除被动画拖慢
                await Task.WhenAny(completion.Task, Task.Delay(160));
            }

            await _viewModel.DeleteRowAsync(row);
        }
    }

    /// <summary>从一个按钮往上找到它所在的 ListBoxItem（行容器）。</summary>
    private FrameworkElement? FindRowContainer(DependencyObject start)
    {
        var current = start;
        while (current is not null and not System.Windows.Controls.ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return current as FrameworkElement;
    }

    // ---------------- 回归测试用入口 ----------------

    /// <summary>给回归用：当前列表里的行数。</summary>
    internal int RowCountForTest() => _viewModel.Rows.Count;

    /// <summary>给回归用：已完成条数（走 VM 的完整数据集，不受当前视图过滤影响）。</summary>
    internal int DoneCountForTest() => _viewModel.CompletedCountForTest;

    /// <summary>给回归用：删除第 index 行（走与 UI 相同的入口）。</summary>
    internal void DeleteRowByIndexForTest(int index)
    {
        if (index >= 0 && index < _viewModel.Rows.Count)
        {
            _ = _viewModel.DeleteRowAsync(_viewModel.Rows[index]);
        }
    }

    /// <summary>给回归用：完成第 index 行。</summary>
    internal void CompleteRowByIndexForTest(int index)
    {
        if (index >= 0 && index < _viewModel.Rows.Count)
        {
            _ = _viewModel.MarkDoneAsync(_viewModel.Rows[index]);
        }
    }

    // ---------------- 表格行内编辑 ----------------

    /// <summary>行标题获得焦点：进入编辑态，全选便于整体替换。</summary>
    private void RowTitle_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: TodoRowViewModel row } box)
        {
            row.IsEditing = true;

            // 若确实要连续录入，按 Enter 会跳到下一行，不要打断
            if (box.SelectionLength == 0 && box.Text.Length > 0 && e.KeyboardDevice.Modifiers == ModifierKeys.None)
            {
                box.SelectAll();
            }
        }
    }

    /// <summary>行标题失焦：提交修改。</summary>
    private async void RowTitle_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: TodoRowViewModel row })
        {
            await _viewModel.CommitTitleAsync(row);
        }
    }

    /// <summary>行标题按键：回车提交并下移；Esc 撤销。</summary>
    private async void RowTitle_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TodoRowViewModel row } box)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await _viewModel.CommitTitleAsync(row);
                FocusNextRowOrDraft(row);
                break;

            case Key.Escape:
                e.Handled = true;
                row.Title = row.Item.Title;
                row.EditError = null;
                break;
        }
    }

    /// <summary>按顺序把焦点移到下一行的标题框，最后一行则落到草稿行。</summary>
    private void FocusNextRowOrDraft(TodoRowViewModel current)
    {
        var index = _viewModel.Rows.IndexOf(current);
        var next = index >= 0 && index + 1 < _viewModel.Rows.Count ? _viewModel.Rows[index + 1] : null;

        Dispatcher.BeginInvoke(() =>
        {
            if (TryFindRowTitle(next) is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
            else
            {
                DraftBox.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private TextBox? TryFindRowTitle(TodoRowViewModel? row)
    {
        if (row is null)
        {
            return null;
        }

        TodoList.UpdateLayout();
        if (TodoList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem container)
        {
            return null;
        }

        return FindDescendant<TextBox>(container);
    }

    private static T? FindDescendant<T>(DependencyObject? current) where T : DependencyObject
    {
        if (current is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void DraftRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
        {
            return;
        }

        DraftBox.Focus();
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase or CheckBox)
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void DraftBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _viewModel.IsDraftFocused = true;

    private async void DraftBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _viewModel.IsDraftFocused = false;
        await _viewModel.CommitDraftAsync();
    }

    /// <summary>草稿行按键：回车新增并保持焦点；Esc 清空。</summary>
    private async void DraftBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;

                var before = _viewModel.Rows.Count;
                await _viewModel.CommitDraftAsync();

                // 新增成功：让新行滑入一下，明确"东西加进去了"
                if (_viewModel.Rows.Count > before)
                {
                    AnimateNewestRow();
                }

                // 新增后把焦点留在草稿行，方便连续录入
                _ = Dispatcher.BeginInvoke(() => DraftBox.Focus(), DispatcherPriority.Input);
                break;

            case Key.Escape:
                e.Handled = true;
                _viewModel.DraftTitle = string.Empty;
                break;
        }
    }

    /// <summary>把最新加入的那一行做个滑入动画（列表数据刚更新，等一帧拿到容器）。</summary>
    private void AnimateNewestRow()
    {
        if (!Motion.Enabled)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            var row = _viewModel.Rows.LastOrDefault();
            if (row is null)
            {
                return;
            }

            if (TodoList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            {
                Motion.SlideInFromTop(container, offset: 10);
            }
        }, DispatcherPriority.Loaded);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 双击标题：改用 Shift+双击 防止误触；这里只处理拖动
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已经提前抬起，忽略
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleTopmost_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleTopmost();

    private void ToggleLock_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleLock();

    // ---------------- 外观：皮肤 + 透明度 ----------------

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        AppearanceMenu.Show(element, _viewModel, _windows);
        e.Handled = true;
    }

    /// <summary>全局快捷键 Ctrl+Alt+N：打开后把光标放到末尾的草稿行。</summary>
    public void FocusNewTodo()
    {
        ShowAndActivate();
        DraftBox.Focus();
        DraftBox.SelectAll();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void ToggleLock() => _viewModel.ToggleLock();

    public string BoardId => _viewModel.BoardId;

    public void RefreshAppearance() => _viewModel.RefreshAppearance();

    /// <summary>给回归脚本用：在第一条待办上弹出自绘右键菜单。</summary>
    public void ShowTodoContextMenuForFirstRow()
    {
        if (_viewModel.Rows.Count == 0)
        {
            return;
        }

        // 走和真实右键一模一样的构造逻辑，只是把数据源换成第一条
        BuildTodoContextMenu(_viewModel.Rows[0], out var menu);
        TodoList.ContextMenu = menu;
        menu.PlacementTarget = TodoList;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        menu.IsOpen = true;
    }

    /// <summary>给回归脚本用：展开菜单里的第一个子菜单（「标签」）。</summary>
    public void OpenFirstSubmenu()
    {
        var menu = TodoList.ContextMenu;
        var submenu = menu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.HasItems);
        if (submenu is null)
        {
            Serilog.Log.Warning("[menu] 未找到带子项的菜单项，menu={Menu} items={Count}",
                menu is not null, menu?.Items.Count ?? -1);
            return;
        }

        submenu.IsSubmenuOpen = true;
        Serilog.Log.Information("[menu] 子菜单「{Header}」 IsSubmenuOpen={Open} 子项数={Count}",
            submenu.Header, submenu.IsSubmenuOpen, submenu.Items.Count);
    }

    /// <summary>给回归脚本用：收起待办的右键菜单。</summary>
    public void CloseTodoContextMenu()
    {
        if (TodoList.ContextMenu is { } menu)
        {
            menu.IsOpen = false;
            TodoList.ContextMenu = null;
        }
    }

    /// <summary>给回归脚本用：弹出「🎨 外观」菜单，验证另一套菜单的配色。</summary>
    public void ShowAppearanceMenuForTest()
    {
        _lastAppearanceMenu = AppearanceMenu.Build(_viewModel, _windows);
        _lastAppearanceMenu.PlacementTarget = TodoList;
        _lastAppearanceMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        _lastAppearanceMenu.IsOpen = true;
    }

    private ContextMenu? _lastAppearanceMenu;

    /// <summary>给回归脚本用：把焦点放到第一行的标题编辑框。</summary>
    public void FocusFirstRowTitle()
    {
        if (_viewModel.Rows.Count == 0)
        {
            return;
        }

        TodoList.UpdateLayout();
        if (TryFindRowTitle(_viewModel.Rows[0]) is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    /// <summary>给回归脚本读：某个标签胶囊的屏幕中心（用于真实鼠标拖到标签的验证）。</summary>
    public (double X, double Y) ChipScreenPoint(string chipName)
    {
        UpdateLayout();
        var root = FindChipsRoot();
        if (root is null)
        {
            return (double.NaN, double.NaN);
        }

        foreach (var button in FindAllDescendants<Button>(root))
        {
            if (button.DataContext is TagChipViewModel chip && chip.Name == chipName)
            {
                var origin = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                var dpi = VisualTreeHelper.GetDpi(this);
                return (origin.X / dpi.DpiScaleX, origin.Y / dpi.DpiScaleY);
            }
        }

        return (double.NaN, double.NaN);
    }

    private DependencyObject? FindChipsRoot() => FindDescendant<ItemsControl>(this, "ChipList");

    /// <summary>
    /// 给回归脚本用：模拟「把第一行拖到名为 chipName 的胶囊上」。
    /// 内部执行的就是 Chip_Drop 里的那段逻辑（打标签 / 清空标签），返回是否执行成功。
    /// </summary>
    public async Task<bool> SimulateDropOnChipAsync(string chipName)
    {
        var row = _viewModel.Rows.FirstOrDefault();
        var chip = _viewModel.Chips.FirstOrDefault(c => c.Name == chipName);
        if (row is null || chip is null)
        {
            Serilog.Log.Warning("[tagdrag] 找不到行或胶囊：row={Row} chip={Chip}", row is not null, chip is not null);
            return false;
        }

        if (chip.IsAll || chip.IsNoTag)
        {
            await _viewModel.ClearTagsAsync(row);
            return true;
        }

        if (chip.TagId is { } tagId)
        {
            await _viewModel.AddTagAsync(row, tagId);
            return true;
        }

        return false;
    }

    /// <summary>给回归脚本用：删除第 N 行。</summary>
    public Task DeleteRowForTestAsync(int rowIndex)
    {
        var row = rowIndex >= 0 && rowIndex < _viewModel.Rows.Count ? _viewModel.Rows[rowIndex] : null;
        return row is null ? Task.CompletedTask : _viewModel.DeleteRowAsync(row);
    }

    /// <summary>给回归脚本用：重新加载（模拟外部改了数据）。</summary>
    public Task ReloadForTestAsync() => _viewModel.LoadAsync();

    /// <summary>给回归脚本读：各标签胶囊的「名称 计数」，用来验证计数是否随标签变化更新。</summary>
    public IReadOnlyList<string> ChipCounts() =>
        _viewModel.Chips.Select(c => $"{c.Name} {c.Count}").ToList();

    /// <summary>给回归脚本用：给第 N 行加标签。</summary>
    public Task AddTagForTestAsync(int rowIndex, string tagId)
    {
        var row = rowIndex >= 0 && rowIndex < _viewModel.Rows.Count ? _viewModel.Rows[rowIndex] : null;
        return row is null ? Task.CompletedTask : _viewModel.AddTagAsync(row, tagId);
    }

    /// <summary>给回归脚本用：清空第 N 行的标签。</summary>
    public Task ClearTagsForTestAsync(int rowIndex)
    {
        var row = rowIndex >= 0 && rowIndex < _viewModel.Rows.Count ? _viewModel.Rows[rowIndex] : null;
        return row is null ? Task.CompletedTask : _viewModel.ClearTagsAsync(row);
    }

    /// <summary>给回归脚本读：某条待办当前挂了哪些标签（用来断言拖动结果）。</summary>
    public string TagTextOfRow(int index) =>
        index >= 0 && index < _viewModel.Rows.Count ? _viewModel.Rows[index].TagText : string.Empty;

    /// <summary>给回归脚本读：直接从数据库读第一条待办的标签（绕开内存缓存，验证真的落库了）。</summary>
    public string TagTextOfFirstRowFromDb() =>
        _viewModel.TagTextOfRowFromDb(0);

    /// <summary>给回归脚本用：切换锁定状态。</summary>
    public void SwitchLockForTest() => _viewModel.ToggleLock();

    /// <summary>给回归脚本读：描述内容区的可见性/尺寸，用来诊断「锁定后白屏」。</summary>
    public string DescribeContentVisibility()
    {
        var content = FindDescendant<Grid>(this, null);
        var list = TodoList;

        var parts = new List<string>
        {
            $"窗口={ActualWidth:0}x{ActualHeight:0}",
            $"列表项={_viewModel.Rows.Count}",
            $"列表={list.ActualWidth:0}x{list.ActualHeight:0}",
            $"列表可见={list.IsVisible}",
            $"列表启用={list.IsEnabled}",
        };

        if (content is not null)
        {
            parts.Add($"内容区={content.ActualWidth:0}x{content.ActualHeight:0}");
            parts.Add($"内容区可见={content.IsVisible}");
            parts.Add($"内容区启用={content.IsEnabled}");
        }

        parts.Add($"草稿行启用={DraftBox.IsEnabled}");
        return string.Join(" ", parts);
    }

    /// <summary>给回归脚本用：模拟鼠标悬停在第 N 行（让 ✓ / ✕ / ↺ 显示出来）。</summary>
    public void ForceRowHover(int index)
    {
        TodoList.UpdateLayout();
        if (index < 0 || index >= _viewModel.Rows.Count)
        {
            return;
        }

        if (TodoList.ItemContainerGenerator.ContainerFromItem(_viewModel.Rows[index]) is ListBoxItem container)
        {
            // IsMouseOver 是只读依赖属性，用一个附加的触发器无法伪造；
            // 这里直接把悬停按钮组的 Opacity 设为 1，等价于视觉结果
            foreach (var panel in FindAllDescendants<StackPanel>(container))
            {
                if (panel.Children.Count > 0 && panel.Children[0] is Button)
                {
                    panel.Opacity = 1;
                }
            }
        }
    }

    private static IEnumerable<T> FindAllDescendants<T>(DependencyObject? current) where T : DependencyObject
    {
        if (current is null)
        {
            yield break;
        }

        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindAllDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>给回归脚本读：第 N 行的屏幕中心（用于真实鼠标拖拽验证）。</summary>
    public (double X, double Y) RowScreenPoint(int index)
    {
        TodoList.UpdateLayout();
        if (index < 0 || index >= _viewModel.Rows.Count)
        {
            return (double.NaN, double.NaN);
        }

        if (TodoList.ItemContainerGenerator.ContainerFromItem(_viewModel.Rows[index]) is not ListBoxItem container)
        {
            return (double.NaN, double.NaN);
        }

        var origin = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
        var dpi = VisualTreeHelper.GetDpi(this);
        return (origin.X / dpi.DpiScaleX, origin.Y / dpi.DpiScaleY);
    }

    /// <summary>给回归脚本读：当前是否还有行处于编辑态。</summary>
    public int EditingRowCount() => _viewModel.Rows.Count(r => r.IsEditing);

    /// <summary>给回归脚本用：把第一行置为编辑态（模拟用户点进输入框）。</summary>
    public void BeginEditFirstRow()
    {
        if (_viewModel.Rows.Count == 0)
        {
            return;
        }

        _viewModel.Rows[0].IsEditing = true;
    }

    /// <summary>给回归脚本用：模拟点击窗口中一块"空白处"，应触发退出编辑。</summary>
    public async Task SimulateBlankAreaClickAsync()
    {
        await CommitEditingRowsAsync();
    }

    /// <summary>给回归脚本读：第一条待办拖动把手在屏幕上的位置（用于真实鼠标拖拽验证）。</summary>
    public (double X, double Y) FirstDragHandlePoint()
    {
        TodoList.UpdateLayout();
        if (_viewModel.Rows.Count == 0 || TodoList.ItemContainerGenerator.ContainerFromItem(_viewModel.Rows[0]) is not ListBoxItem container)
        {
            return (double.NaN, double.NaN);
        }

        var handle = FindDescendant<FrameworkElement>(container, "DragHandle");
        if (handle is null)
        {
            return (double.NaN, double.NaN);
        }

        var origin = handle.PointToScreen(new Point(handle.ActualWidth / 2, handle.ActualHeight / 2));
        var dpi = VisualTreeHelper.GetDpi(this);
        return (origin.X / dpi.DpiScaleX, origin.Y / dpi.DpiScaleY);
    }

    private static T? FindDescendant<T>(DependencyObject? current, string? name = null) where T : FrameworkElement
    {
        if (current is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child is T match && (name is null || match.Name == name))
            {
                return match;
            }

            if (FindDescendant<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>当前列表里显示的行（给回归脚本读，验证视图切换真的换了内容）。</summary>
    public IReadOnlyList<string> CurrentRowSnapshot() =>
        _viewModel.Rows.Select(r => r.Title).ToList();

    /// <summary>输入区状态（给回归脚本读，验证已完成视图下不能新增）。</summary>
    public (bool InputEnabled, bool AddEnabled) CurrentInputState() =>
        (DraftBox.IsEnabled, _viewModel.CanAdd);

    /// <summary>切换视图（回归脚本用）。</summary>
    public void SwitchView(TodoViewMode mode)
    {
        _viewModel.SwitchView(mode);
        RefreshViewTabs();
    }

    // ---------------- 位置记忆与吸附 ----------------

    private void RestoreGeometry()
    {
        if (_geometryRestored)
        {
            return;
        }

        _geometryRestored = true;

        if (_viewModel.Board.Window is { IsValid: true } state)
        {
            Width = state.Width;
            Height = state.Height;
            var (left, top) = ScreenHelper.ClampIntoWorkArea(this, state.Left, state.Top, state.Width, state.Height);
            Left = left;
            Top = top;
            return;
        }

        // 没记录过：从管理面板附近层叠排开
        var offset = CascadeStep * (_windows.OpenWindowCount + 1);
        var (cascadeLeft, cascadeTop) = ScreenHelper.ClampIntoWorkArea(this, 60 + offset, 60 + offset, Width, Height);
        Left = cascadeLeft;
        Top = cascadeTop;
    }

    /// <summary>便签纸面（不含阴影留白）在屏幕上的矩形。</summary>
    private SnapRect CurrentNoteRect() => new(
        Left + ShadowMargin,
        Top + ShadowMargin,
        Math.Max(1, ActualWidth - (ShadowMargin * 2)),
        Math.Max(1, ActualHeight - (ShadowMargin * 2)));

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_geometryRestored || _isSnapping || _isFullScreen)
        {
            return;
        }

        SnapToNeighbours();
        ScheduleSave();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_geometryRestored)
        {
            return;
        }

        ScheduleSave();
    }

    private void SnapToNeighbours()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
        {
            return;
        }

        var note = CurrentNoteRect();
        var workArea = ScreenHelper.GetWorkArea(this);
        var others = _windows.GetOtherWindowRects(this, ShadowMargin);

        var snapped = WindowSnapper.Snap(note, workArea, others, SnapThreshold);
        if (Math.Abs(snapped.Left - note.Left) < 0.5 && Math.Abs(snapped.Top - note.Top) < 0.5)
        {
            return;
        }

        _isSnapping = true;
        try
        {
            Left = snapped.Left - ShadowMargin;
            Top = snapped.Top - ShadowMargin;
        }
        finally
        {
            _isSnapping = false;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveGeometry()
    {
        if (!_geometryRestored || WindowState != WindowState.Normal)
        {
            return;
        }

        _viewModel.Board.Window = new BoardWindowState(Left, Top, ActualWidth, ActualHeight);
        _windows.SaveBoardGeometry(_viewModel.Board);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _saveTimer.Stop();
        SaveGeometry();
        base.OnClosing(e);
    }

    // ---------------- 无边框窗口的边缘缩放 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest || _isFullScreen || ResizeMode == ResizeMode.NoResize)
        {
            return IntPtr.Zero;
        }

        long raw = lParam.ToInt64();
        var screenPoint = new NativePoint
        {
            X = (int)(short)(raw & 0xFFFF),
            Y = (int)(short)((raw >> 16) & 0xFFFF),
        };

        if (!NativeMethods.ScreenToClient(hwnd, ref screenPoint))
        {
            return IntPtr.Zero;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = screenPoint.X / dpi.DpiScaleX;
        var y = screenPoint.Y / dpi.DpiScaleY;
        var width = ActualWidth;
        var height = ActualHeight;

        var onLeft = x <= ResizeBorder;
        var onRight = x >= width - ResizeBorder;
        var onTop = y <= ResizeBorder;
        var onBottom = y >= height - ResizeBorder;

        var hit = onTop && onLeft ? HtTopLeft
            : onTop && onRight ? HtTopRight
            : onBottom && onLeft ? HtBottomLeft
            : onBottom && onRight ? HtBottomRight
            : onLeft ? HtLeft
            : onRight ? HtRight
            : onTop ? HtTop
            : onBottom ? HtBottom
            : 0;

        if (hit == 0)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(hit);
    }

    // ---------------- 标签 ----------------

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChipViewModel chip })
        {
            _ = _viewModel.SelectChipAsync(chip);
        }
    }

    /// <summary>把待办拖到标签胶囊上：拖过时高亮，松手即改标签。</summary>
    /// <summary>拖动经过胶囊时给它一个明显的视觉反馈。</summary>
    private void Chip_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Button element && e.Data.GetDataPresent(typeof(TodoRowViewModel)))
        {
            element.Opacity = 1.0;
            element.ScaleTransformForDrag(1.08);
            Serilog.Log.Debug("[tag] DragEnter：胶囊接受拖动");
        }
    }

    private void Chip_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button element)
        {
            element.Opacity = 1.0;
            element.ScaleTransformForDrag(1.0);
        }
    }

    /// <summary>
    /// 把待办拖到标签胶囊上：拖过时高亮，松手即改标签。
    /// 注意 effects 必须和拖源头一致（都是 Move），否则交集为空、Drop 根本不会触发。
    /// </summary>
    private void Chip_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Button element)
        {
            return;
        }

        var hasRow = e.Data.GetDataPresent(typeof(TodoRowViewModel));
        e.Effects = hasRow ? DragDropEffects.Move : DragDropEffects.None;
        Serilog.Log.Debug("[tag] DragOver：hasRow={Has} effects={Effects}", hasRow, e.Effects);
        e.Handled = true;
    }

    private async void Chip_Drop(object sender, DragEventArgs e)
    {
        Serilog.Log.Information("[tag] Drop 被触发");

        if (sender is not Button element)
        {
            return;
        }

        element.ScaleTransformForDrag(1.0);
        element.Opacity = 1.0;

        if (element.DataContext is not TagChipViewModel chip)
        {
            return;
        }

        if (e.Data.GetData(typeof(TodoRowViewModel)) is not TodoRowViewModel row)
        {
            return;
        }

        e.Handled = true;

        // "全部" / "未分类"：清空这条待办的标签
        if (chip.IsAll || chip.IsNoTag)
        {
            await _viewModel.ClearTagsAsync(row);
            return;
        }

        if (chip.TagId is { } tagId)
        {
            await _viewModel.AddTagAsync(row, tagId);
        }
    }

    private void Chip_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagChipViewModel chip } element || !chip.IsRealTag)
        {
            return;
        }

        var tag = _viewModel.Tags.FirstOrDefault(t => t.Id == chip.TagId);
        if (tag is null)
        {
            return;
        }

        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "重命名标签…" };
        rename.Click += async (_, _) =>
        {
            var name = PromptWindow.Prompt(this, "重命名标签", "标签名称", tag.Name);
            if (name is not null)
            {
                await _viewModel.RenameTagAsync(tag, name);
            }
        };

        var remove = new MenuItem { Header = "删除标签" };
        remove.Click += async (_, _) =>
        {
            var confirm = MessageBox.Show(
                $"删除标签「{tag.Name}」？已挂该标签的待办不受影响。",
                "确认删除",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (confirm == MessageBoxResult.OK)
            {
                await _viewModel.DeleteTagAsync(tag);
            }
        };

        menu.Items.Add(rename);
        menu.Items.Add(remove);
        menu.PlacementTarget = element;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void AddTag_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Prompt(this, "新建标签", "标签名称");
        if (name is not null)
        {
            await _viewModel.CreateTagAsync(name);
        }
    }

    private void OpenManager_Click(object sender, RoutedEventArgs e) =>
        (System.Windows.Application.Current.MainWindow as ManagerWindow)?.ShowAndActivate();

    // ---------------- 右键：标签 / 删除 ----------------

    private void TodoList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextRow = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as TodoRowViewModel;
    }

    private void TodoList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_viewModel.IsLocked)
        {
            e.Handled = true;
            return;
        }

        if (_contextRow is not { } row)
        {
            e.Handled = true;
            return;
        }

        BuildTodoContextMenu(row, out var menu);
        TodoList.ContextMenu = menu;
    }

    /// <summary>
    /// 构造待办的右键菜单。抽出来是为了让回归脚本能复用同一套逻辑生成菜单并截图。
    /// </summary>
    private void BuildTodoContextMenu(TodoRowViewModel row, out ContextMenu menu)
    {
        menu = new ContextMenu();

        var toggle = new MenuItem { Header = row.IsDone ? "标记为未完成" : "标记为已完成" };
        toggle.Click += async (_, _) => await _viewModel.ToggleAsync(row);
        menu.Items.Add(toggle);

        var tagMenu = new MenuItem { Header = "标签" };
        foreach (var tag in _viewModel.Tags)
        {
            var item = new MenuItem
            {
                Header = tag.Name,
                IsCheckable = true,
                IsChecked = _viewModel.HasTag(row, tag),
                StaysOpenOnClick = true,
            };
            item.Click += async (_, _) => await _viewModel.ToggleTagAsync(row, tag);
            tagMenu.Items.Add(item);
        }

        if (tagMenu.Items.Count == 0)
        {
            tagMenu.Items.Add(new MenuItem { Header = "（还没有标签）", IsEnabled = false });
        }

        tagMenu.Items.Add(new Separator());
        var newTag = new MenuItem { Header = "新建标签…" };
        newTag.Click += async (_, _) =>
        {
            var name = PromptWindow.Prompt(this, "新建标签", "标签名称");
            if (name is not null)
            {
                await _viewModel.CreateTagAsync(name);
            }
        };
        tagMenu.Items.Add(newTag);
        menu.Items.Add(tagMenu);

        // 目标日期（倒数日）
        var dueMenu = new MenuItem { Header = "目标日期" };
        var today = DateTimeOffset.Now.Date;
        dueMenu.Items.Add(BuildDateItem("今天", today, row));
        dueMenu.Items.Add(BuildDateItem("明天", today.AddDays(1), row));
        dueMenu.Items.Add(BuildDateItem("本周末", NextWeekend(today), row));

        var customDue = new MenuItem { Header = "自定义…" };
        customDue.Click += async (_, _) =>
        {
            var text = PromptWindow.Prompt(this, "目标日期", "格式 yyyy-MM-dd", today.ToString("yyyy-MM-dd"));
            if (text is not null && DateTimeOffset.TryParse(text, out var parsed))
            {
                await _viewModel.SetDueAsync(row, parsed);
            }
        };
        dueMenu.Items.Add(customDue);

        if (row.Item.DueAt is not null)
        {
            var clearDue = new MenuItem { Header = "清除目标日期" };
            clearDue.Click += async (_, _) => await _viewModel.SetDueAsync(row, null);
            dueMenu.Items.Add(new Separator());
            dueMenu.Items.Add(clearDue);
        }

        menu.Items.Add(dueMenu);

        // 提醒
        var remindMenu = new MenuItem { Header = "提醒" };
        var now = DateTimeOffset.Now;
        remindMenu.Items.Add(BuildRemindItem("30 分钟后", now.AddMinutes(30), row));
        remindMenu.Items.Add(BuildRemindItem("1 小时后", now.AddHours(1), row));
        remindMenu.Items.Add(BuildRemindItem("今天 18:00", today.AddHours(18), row));
        remindMenu.Items.Add(BuildRemindItem("明天 9:00", today.AddDays(1).AddHours(9), row));

        var customRemind = new MenuItem { Header = "自定义…" };
        customRemind.Click += async (_, _) =>
        {
            var text = PromptWindow.Prompt(this, "提醒时间", "格式 yyyy-MM-dd HH:mm",
                now.AddHours(1).ToString("yyyy-MM-dd HH:mm"));
            if (text is not null && DateTimeOffset.TryParse(text, out var parsed))
            {
                await _viewModel.SetRemindAsync(row, parsed);
            }
        };
        remindMenu.Items.Add(customRemind);

        if (row.Item.RemindAt is not null)
        {
            var clearRemind = new MenuItem { Header = "清除提醒" };
            clearRemind.Click += async (_, _) => await _viewModel.SetRemindAsync(row, null);
            remindMenu.Items.Add(new Separator());
            remindMenu.Items.Add(clearRemind);
        }

        menu.Items.Add(remindMenu);

        // 重复规则（RFC 5545 RRULE）
        menu.Items.Add(BuildRecurrenceMenu(row));

        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "删除待办" };
        remove.Click += async (_, _) => await _viewModel.DeleteAsync(row);
        menu.Items.Add(remove);
    }

    private static DateTimeOffset NextWeekend(DateTimeOffset today)
    {
        var delta = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(delta == 0 ? 7 : delta);
    }

    private MenuItem BuildDateItem(string header, DateTimeOffset value, TodoRowViewModel row)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = row.Item.DueAt is { } due && due.Date == value.Date,
        };
        item.Click += async (_, _) => await _viewModel.SetDueAsync(row, value);
        return item;
    }

    /// <summary>重复规则菜单：常用预设 + 自定义 RRULE + 清除。</summary>
    private MenuItem BuildRecurrenceMenu(TodoRowViewModel row)
    {
        var rule = row.Item.Recurrence;
        var menu = new MenuItem
        {
            Header = string.IsNullOrWhiteSpace(rule)
                ? "重复"
                : $"重复（{RecurrenceEngine.Describe(rule)}）",
        };

        foreach (var (name, preset) in RecurrenceEngine.Presets)
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(rule, preset, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += async (_, _) => await _viewModel.SetRecurrenceAsync(row, preset);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var custom = new MenuItem { Header = "自定义规则…" };
        custom.Click += async (_, _) =>
        {
            var text = PromptWindow.Prompt(
                this,
                "自定义重复规则",
                "RFC 5545 RRULE，例如 FREQ=WEEKLY;BYDAY=MO,WE",
                rule ?? "FREQ=WEEKLY");

            if (text is null)
            {
                return;
            }

            if (!RecurrenceEngine.IsValid(text))
            {
                MessageBox.Show($"这条规则看不懂：{text}", "规则无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _viewModel.SetRecurrenceAsync(row, text);
        };
        menu.Items.Add(custom);

        if (!string.IsNullOrWhiteSpace(rule))
        {
            var clear = new MenuItem { Header = "不再重复" };
            clear.Click += async (_, _) => await _viewModel.SetRecurrenceAsync(row, null);
            menu.Items.Add(new Separator());
            menu.Items.Add(clear);
        }

        return menu;
    }

    private MenuItem BuildRemindItem(string header, DateTimeOffset value, TodoRowViewModel row)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = row.Item.RemindAt is { } remind
                        && Math.Abs((remind - value).TotalMinutes) < 1,
        };
        item.Click += async (_, _) => await _viewModel.SetRemindAsync(row, value);
        return item;
    }

    // ---------------- 拖拽排序 / 拖到标签改标签 ----------------

    /// <summary>拖动把手按下：只在这里武装拖拽，避免和行内编辑抢鼠标。</summary>
    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as TodoRowViewModel;
        if (row is null || _viewModel.IsLocked)
        {
            _dragArmed = false;
            return;
        }

        _dragCandidate = row;
        _dragStart = e.GetPosition(TodoList);
        _dragArmed = true;

        // 不要在把手上触发别的选择/编辑行为
        e.Handled = true;
    }

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(TodoList);
        if (Math.Abs(position.X - _dragStart.X) < 4 && Math.Abs(position.Y - _dragStart.Y) < 4)
        {
            return;
        }

        _dragArmed = false;
        DragDrop.DoDragDrop((DependencyObject)sender, _dragCandidate, DragDropEffects.Move);
    }

    private void TodoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
    }

    /// <summary>点空白处：提交正在编辑的行，并把焦点从输入框挪走。</summary>
    private async void CommitAllRowsAndClearFocus()
    {
        await CommitEditingRowsAsync();
    }

    private void TodoList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TodoRowViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;

        // 悬停到某一行时给它画一条"插入线"
        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as TodoRowViewModel;
        foreach (var row in _viewModel.Rows)
        {
            row.IsDragTarget = ReferenceEquals(row, target);
        }

        e.Handled = true;
    }

    private void TodoList_DragLeave(object sender, DragEventArgs e)
    {
        foreach (var row in _viewModel.Rows)
        {
            row.IsDragTarget = false;
        }
    }

    private async void TodoList_Drop(object sender, DragEventArgs e)
    {
        foreach (var row in _viewModel.Rows)
        {
            row.IsDragTarget = false;
        }

        if (e.Data.GetData(typeof(TodoRowViewModel)) is not TodoRowViewModel moving)
        {
            return;
        }

        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as TodoRowViewModel;
        await _viewModel.MoveBeforeAsync(moving.Id, target?.Id);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}