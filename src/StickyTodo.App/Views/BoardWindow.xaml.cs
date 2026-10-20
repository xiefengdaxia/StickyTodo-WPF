using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
public partial class BoardWindow : Window
{
    private const double SnapThreshold = 10d;
    private const int CascadeStep = 28;

    private readonly BoardViewModel _viewModel;
    private readonly WindowManager _windows;
    private readonly DispatcherTimer _saveTimer;

    private bool _isSnapping;
    private bool _geometryRestored;

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

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_geometryRestored || _isSnapping)
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

        var current = new SnapRect(Left, Top, ActualWidth, ActualHeight);
        var workArea = ScreenHelper.GetWorkArea(this);
        var others = _windows.GetOtherWindowRects(this);

        var snapped = WindowSnapper.Snap(current, workArea, others, SnapThreshold);
        if (Math.Abs(snapped.Left - Left) < 0.5 && Math.Abs(snapped.Top - Top) < 0.5)
        {
            return;
        }

        _isSnapping = true;
        try
        {
            Left = snapped.Left;
            Top = snapped.Top;
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

        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "删除待办" };
        remove.Click += async (_, _) => await _viewModel.DeleteAsync(row);
        menu.Items.Add(remove);

        TodoList.ContextMenu = menu;
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