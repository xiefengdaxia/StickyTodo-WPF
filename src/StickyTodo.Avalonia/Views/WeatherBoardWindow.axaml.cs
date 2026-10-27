using Avalonia.Controls;
using Avalonia.Interactivity;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>天气看板窗口。不绑定看板数据，走独立的简化窗口。</summary>
public partial class WeatherBoardWindow : Window
{
    private readonly WeatherViewModel _viewModel;

    public WeatherBoardWindow(WeatherViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 小屏 / 高缩放下把窗口收进工作区，否则标题栏会被顶到屏幕外（看着像"拖不动"）
        Opened += (_, _) => Services.ScreenHelper.FitIntoWorkArea(this);

        Opened += async (_, _) => await viewModel.LoadCoreAsync();
    }

    private void TitleBar_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

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
            // 忽略
        }
    }

    private void City_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: WeatherCityRow city })
        {
            _viewModel.SelectCity(city.Name);
        }
    }

    /// <summary>输入城市名后按回车 = 点「添加」。</summary>
    private void NewCity_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            _viewModel.AddCityFromInputCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Appearance_Click(object? sender, RoutedEventArgs e)
    {
        if (App.Services is { } services)
        {
            var menu = Views.WeatherMenuBuilder.BuildMenu(services.Skin, () => services.Windows.ApplySkin());
            menu.PlacementTarget = this;
            menu.Open(this);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
