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
        };

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

    // ---------------- 标题栏拖动 / 关闭 ----------------

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

    /// <summary>全局快捷键 Ctrl+Alt+N：打开后把光标放到新增输入框。</summary>
    public void FocusNewTodo()
    {
        ShowAndActivate();
        InputBox.Focus();
        InputBox.SelectAll();
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

    private async void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChipViewModel chip })
        {
            await _viewModel.SelectChipAsync(chip);
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

        var menu = new ContextMenu();

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

        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "删除待办" };
        remove.Click += async (_, _) => await _viewModel.DeleteAsync(row);
        menu.Items.Add(remove);

        TodoList.ContextMenu = menu;
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

    // ---------------- 拖拽排序 ----------------

    private void TodoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // 点在复选框/按钮上时不启动拖拽，避免抢掉点击
        if (FindAncestor<CheckBox>(source) is not null || FindAncestor<Button>(source) is not null)
        {
            _dragArmed = false;
            return;
        }

        _dragCandidate = FindAncestor<ListBoxItem>(source)?.DataContext as TodoRowViewModel;
        _dragStart = e.GetPosition(TodoList);
        _dragArmed = _dragCandidate is not null;
    }

    private void TodoList_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(TodoList);
        if (Math.Abs(position.X - _dragStart.X) < 6 && Math.Abs(position.Y - _dragStart.Y) < 6)
        {
            return;
        }

        _dragArmed = false;
        DragDrop.DoDragDrop(TodoList, _dragCandidate, DragDropEffects.Move);
    }

    private void TodoList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TodoRowViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void TodoList_Drop(object sender, DragEventArgs e)
    {
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