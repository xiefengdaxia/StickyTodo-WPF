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
}