using System.Windows;
using System.Windows.Input;
using StickyTodo.App.ViewModels;

namespace StickyTodo.App.Views;

/// <summary>独立设置页：外观、快捷键、数据。无边框自绘标题栏。</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += async (_, _) => await _viewModel.RefreshStatsAsync();
    }

    private void Skin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkinOptionViewModel option })
        {
            _viewModel.SelectSkin(option);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>地址框失焦时保存。</summary>
    private void TokenDatabaseBox_LostFocus(object sender, RoutedEventArgs e) => _viewModel.CommitTokenDatabasePath();

    /// <summary>回车也当保存，顺便把焦点移开。</summary>
    private void TokenDatabaseBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _viewModel.CommitTokenDatabasePath();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    /// <summary>
    /// 关闭时只隐藏、不销毁。
    /// 之前没拦这一下：窗口被 Close() 后仍是 DI 单例，下次 ShowSettings 再 Show() 会抛
    /// InvalidOperationException（不能对已关闭的窗口调用 Show），当时也没有全局异常处理，
    /// 表现就是卡一下然后进程直接退出。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private bool _allowClose;

    /// <summary>真正销毁（退出程序时用）。</summary>
    public void RequestClose()
    {
        _allowClose = true;
        Close();
    }
}