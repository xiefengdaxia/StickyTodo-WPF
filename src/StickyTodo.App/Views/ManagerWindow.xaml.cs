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
    }

    /// <summary>外部（比如演示数据种完之后）要求刷新列表。</summary>
    public async Task RefreshAsync()
    {
        await _viewModel.LoadAsync();
    }

    private Board? SelectedBoard => _viewModel.SelectedBoard?.Board;

    // ---------------- 标题栏按钮 ----------------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInteractive(source))
        {
            return;
        }

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
            if (current is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.ListBoxItem)
            {
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