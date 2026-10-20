using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Views;

/// <summary>
/// 主窗体：无边框，左侧操作区 + 右侧看板卡片列表。
/// 点 X 收进托盘，退出走左侧「退出」按钮或托盘菜单。
/// </summary>
public partial class ManagerWindow : Window
{
    private readonly ManagerViewModel _viewModel;
    private readonly WindowManager _windows;
    private bool _allowClose;

    public ManagerWindow(ManagerViewModel viewModel, WindowManager windows)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _windows = windows;
        DataContext = viewModel;

        Loaded += async (_, _) => await _viewModel.LoadAsync();

        // 整个窗口都能拖（含圆角外的那圈边距），只要不是点在交互控件上
        PreviewMouseLeftButtonDown += Window_PreviewMouseLeftButtonDown;
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInteractive(source))
        {
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标提前抬起，忽略
        }
    }

    /// <summary>给回归脚本用：在第一张看板卡片上弹出右键菜单，返回菜单项文字。</summary>
    public IReadOnlyList<string> ShowFirstRowContextMenu()
    {
        BoardList.UpdateLayout();
        if (_viewModel.Boards.Count == 0)
        {
            return [];
        }

        var menu = BoardList.ContextMenu;
        if (menu is null)
        {
            return [];
        }

        _contextRow = _viewModel.Boards[0];
        BoardList.SelectedIndex = 0;

        menu.PlacementTarget = BoardList;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        menu.IsOpen = true;

        return menu.Items
            .OfType<System.Windows.Controls.MenuItem>()
            .Select(i => i.Header?.ToString() ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>外部（比如演示数据种完之后）要求刷新列表。</summary>
    public async Task RefreshAsync()
    {
        await _viewModel.LoadAsync();
    }

    private Board? SelectedBoard => _viewModel.SelectedBoard?.Board;

    // ---------------- 标题栏按钮 ----------------

    /// <summary>
    /// 拖动窗体：只要鼠标按在"非交互控件"上就拖动。
    /// 之前只在标题栏绑定，导致列表区、空白区都拖不动。
    /// </summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮 / 列表项 / 输入框上时不抢事件
        if (e.OriginalSource is DependencyObject source && IsInteractive(source))
        {
            return;
        }

        // 双击标题区 = 最大化/还原
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>点到按钮/列表项上时不要把拖动抢走。</summary>
    private static bool IsInteractive(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            switch (current)
            {
                case System.Windows.Controls.Primitives.ButtonBase:
                case System.Windows.Controls.Primitives.TextBoxBase:
                case System.Windows.Controls.Primitives.ScrollBar:
                case System.Windows.Controls.ListBoxItem:
                    return true;
            }

            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        Shell.Margin = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(12);
        Shell.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(14);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private static void ExitApp()
    {
        System.Windows.Application.Current?.Shutdown();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        (System.Windows.Application.Current as App)?.ShowSettings();

    // ---------------- 看板操作 ----------------

    private async void CreateTodo_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Todo);

    private async void CreateMemo_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Memo);

    private async void CreateCalendar_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Calendar);

    private async Task CreateBoardAsync(BoardKind kind)
    {
        var (title, initial) = kind switch
        {
            BoardKind.Memo => ("新建便签看板", "新便签"),
            BoardKind.Calendar => ("新建日历看板", "日历"),
            _ => ("新建待办看板", "新看板"),
        };

        var name = PromptWindow.Prompt(this, title, "看板名称", initial);
        if (name is null)
        {
            return;
        }

        var board = await _viewModel.CreateBoardAsync(name, kind);
        if (board is not null)
        {
            _windows.OpenBoard(board);
        }
    }

    private async void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BoardRowViewModel row })
        {
            return;
        }

        await DeleteBoardAsync(row.Board);
    }

    // ---------------- 卡片右键菜单 ----------------

    private BoardRowViewModel? _contextRow;

    /// <summary>右键先把被点的那张卡片选中，菜单动作才能作用到正确的看板。</summary>
    private void BoardList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject);
        _contextRow = item?.DataContext as BoardRowViewModel;

        if (item is not null)
        {
            item.IsSelected = true;
        }
        else
        {
            e.Handled = true;
        }
    }

    private void BoardList_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (_contextRow is null)
        {
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private BoardRowViewModel? MenuRow => _contextRow ?? _viewModel.SelectedBoard;

    private void RowMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow is { } row)
        {
            _windows.OpenBoard(row.Board);
        }
    }

    private async void RowMenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow is not { } row)
        {
            return;
        }

        await RenameBoardAsync(row.Board);
    }

    private async void RowMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow is not { } row)
        {
            return;
        }

        await DeleteBoardAsync(row.Board);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBoard is not { } board)
        {
            Warn("先选中一个看板。");
            return;
        }

        await DeleteBoardAsync(board);
    }

    private async Task DeleteBoardAsync(Board board)
    {
        var confirm = MessageBox.Show(
            $"删除看板「{board.Name}」？\n其中的内容会一并删除，且不可恢复。",
            "确认删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var succeeded = await _viewModel.DeleteBoardAsync(board);
        if (succeeded)
        {
            _windows.CloseBoard(board.Id);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBoard is not { } board)
        {
            Warn("先选中一个看板。");
            return;
        }

        await RenameBoardAsync(board);
    }

    /// <summary>重命名（左侧按钮和卡片右键共用）。</summary>
    private async Task RenameBoardAsync(Board board)
    {
        var name = PromptWindow.Prompt(this, "重命名看板", "看板名称", board.Name);
        if (name is null)
        {
            return;
        }

        await _viewModel.RenameBoardAsync(board, name);
        _windows.CloseBoard(board.Id);
        if (_viewModel.SelectedBoard is { } row)
        {
            _windows.OpenBoard(row.Board);
        }
    }

    private void BoardList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedBoard is not { } board)
        {
            return;
        }

        _windows.OpenBoard(board);
    }

    private static void Warn(string message) =>
        MessageBox.Show(message, "StickyTodo", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------------- 生命周期 ----------------

    /// <summary>点 X 收进托盘；退出走左侧按钮或托盘菜单。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void RequestClose()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>从便签窗口的「⋯」按钮唤起管理面板。</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}