using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Views;

/// <summary>看板管理面板：列表 + 新建/重命名/删除/打开。</summary>
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

    private Board? SelectedBoard => _viewModel.SelectedBoard?.Board;

    private async void CreateTodo_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Todo);

    private async void CreateMemo_Click(object sender, RoutedEventArgs e) =>
        await CreateBoardAsync(BoardKind.Memo);

    private async Task CreateBoardAsync(BoardKind kind)
    {
        var name = PromptWindow.Prompt(
            this,
            kind == BoardKind.Memo ? "新建便签看板" : "新建待办看板",
            "看板名称",
            kind == BoardKind.Memo ? "新便签" : "新看板");

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

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var board = SelectedBoard;
        if (board is null)
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

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var board = SelectedBoard;
        if (board is null)
        {
            Warn("先选中一个看板。");
            return;
        }

        var confirm = MessageBox.Show(
            $"删除看板「{board.Name}」？\n其中的待办会一并删除，且不可恢复。",
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

    private void BoardList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (SelectedBoard is not { } board)
        {
            Warn("先选中一个看板。");
            return;
        }

        _windows.OpenBoard(board);
    }

    private static void Warn(string message) =>
        MessageBox.Show(message, "StickyTodo", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>点 X 收进托盘；退出走托盘菜单。</summary>
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