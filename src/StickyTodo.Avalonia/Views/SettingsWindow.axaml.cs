using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>设置页：外观 / 启动 / Token / 快捷键 / 数据。</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        TitleBar.PointerPressed += OnTitleBarPressed;

        Opened += async (_, _) =>
        {
            await _viewModel.RefreshStatsAsync();
            SkinList.ItemsSource = _viewModel.Skins;
        };
        Chrome.Apply(this, Shell, 14, 12);
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

        try
        {
            BeginMoveDrag(e);
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private void Skin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: SkinOptionViewModel option })
        {
            _viewModel.SelectSkin(option);
        }
    }

    private void TokenDatabaseBox_LostFocus(object? sender, RoutedEventArgs e) => _viewModel.CommitTokenDatabasePath();

    private void TokenDatabaseBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel.CommitTokenDatabasePath();
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
