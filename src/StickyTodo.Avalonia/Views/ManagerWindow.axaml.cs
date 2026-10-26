using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>管理面板：看板清单 + 新建/改名/删除 + 打开设置与退出。</summary>
public partial class ManagerWindow : Window
{
    private readonly ManagerViewModel _viewModel;

    public ManagerWindow(ManagerViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        TitleBar.PointerPressed += OnTitleBarPressed;
        TitleBar2.PointerPressed += OnTitleBarPressed;

        Opened += async (_, _) => await RefreshAsync();
        Chrome.Apply(this, Shell, 14, 12);
    }

    public ManagerViewModel ViewModel => _viewModel;

    /// <summary>重新读数据库并刷新列表。</summary>
    public async Task RefreshAsync()
    {
        await _viewModel.LoadAsync();
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 点在按钮上不拖动
        if (e.Source is Control source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is Button)
                {
                    return;
                }

                current = current.Parent as Control;
            }
        }

        try
        {
            BeginMoveDrag(e);
        }
        catch (Exception)
        {
            // 某些合成/远程桌面场景下不支持拖动，忽略
        }
    }

    /// <summary>
    /// 列表空白处也能拖动窗口：点在卡片上仍然交给列表处理选中/双击，
    /// 点在空白处则移动窗口（和资源管理器里拖空白处一个手感）。
    /// </summary>
    private void BoardList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.Source as Control;
        while (current is not null)
        {
            // 落在卡片或按钮上：不接手，交给控件自己
            if (current is ListBoxItem or Button)
            {
                return;
            }

            current = current.Parent as Control;
        }

        try
        {
            BeginMoveDrag(e);
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    // ---------------- 新建 ----------------

    private async void CreateTodo_Click(object? sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Todo, Loc.T("manager.newTodo"), Loc.T("manager.newBoardTitle"));

    private async void CreateMemo_Click(object? sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Memo, Loc.T("manager.newMemo"), Loc.T("manager.newMemoDefault"));

    private async void CreateCalendar_Click(object? sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Calendar, Loc.T("manager.newCalendar"), Loc.T("manager.newCalendarDefault"));

    private async void CreateToken_Click(object? sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Token, Loc.T("manager.newToken"), "Token");

    private async Task CreateBoardAsync(BoardKind kind, string title, string initial)
    {
        var name = await PromptWindow.PromptAsync(this, title, Loc.T("manager.boardName"), initial);
        if (name is null)
        {
            return;
        }

        var board = await _viewModel.CreateBoardAsync(name, kind);
        if (board is not null)
        {
            App.Services?.OpenBoard(board);
        }
    }

    // ---------------- 选中项操作 ----------------

    private async void Rename_Click(object? sender, RoutedEventArgs e)
    {
        var board = _viewModel.SelectedBoard?.Board;
        if (board is null)
        {
            _viewModel.ErrorText = Loc.T("manager.pickFirst");
            return;
        }

        var name = await PromptWindow.PromptAsync(this, Loc.T("manager.renameBoardTitle"), Loc.T("manager.boardName"), board.Name);
        if (name is not null)
        {
            await _viewModel.RenameBoardAsync(board, name);
        }
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        var board = _viewModel.SelectedBoard?.Board;
        if (board is null)
        {
            _viewModel.ErrorText = Loc.T("manager.pickFirst");
            return;
        }

        await DeleteBoardAsync(board);
    }

    private async void DeleteRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: BoardRowViewModel row })
        {
            await DeleteBoardAsync(row.Board);
        }
    }

    private async Task DeleteBoardAsync(Board board)
    {
        var confirmed = await ConfirmWindow.ConfirmAsync(
            this,
            Loc.T("manager.deleteBoard"),
            Loc.F("manager.deleteConfirm", board.Name));

        if (!confirmed)
        {
            return;
        }

        App.Services?.CloseBoard(board.Id);
        await _viewModel.DeleteBoardAsync(board);
    }

    // ---------------- 打开 ----------------

    private void BoardList_DoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

    private void RowMenuOpen_Click(object? sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        var board = _viewModel.SelectedBoard?.Board;
        if (board is not null)
        {
            App.Services?.OpenBoard(board);
        }
    }

    private async void RowMenuRename_Click(object? sender, RoutedEventArgs e)
    {
        var board = _viewModel.SelectedBoard?.Board;
        if (board is null)
        {
            return;
        }

        var name = await PromptWindow.PromptAsync(this, Loc.T("manager.renameBoardTitle"), Loc.T("manager.boardName"), board.Name);
        if (name is not null)
        {
            await _viewModel.RenameBoardAsync(board, name);
        }
    }

    private async void RowMenuDelete_Click(object? sender, RoutedEventArgs e)
    {
        var board = _viewModel.SelectedBoard?.Board;
        if (board is null)
        {
            return;
        }

        await DeleteBoardAsync(board);
    }

    // ---------------- 窗口按钮 ----------------

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Hide_Click(object? sender, RoutedEventArgs e) => Hide();

    private void OpenSettings_Click(object? sender, RoutedEventArgs e) => App.Services?.ShowSettings();

    private void Exit_Click(object? sender, RoutedEventArgs e) => App.Services?.ExitApplication();
}
