using System.ComponentModel;
using System.Windows;
using StickyTodo.App.ViewModels;

namespace StickyTodo.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += async (_, _) =>
        {
            await _viewModel.LoadAsync();
            InputBox.Focus();
        };
    }

    /// <summary>点 X 只是收进托盘；真正退出走托盘菜单。</summary>
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

    /// <summary>托盘「退出」调用，允许真正关闭窗口。</summary>
    public void RequestClose()
    {
        _allowClose = true;
        Close();
    }
}