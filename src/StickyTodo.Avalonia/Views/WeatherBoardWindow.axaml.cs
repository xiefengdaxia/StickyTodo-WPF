using Avalonia.Controls;
using Avalonia.Interactivity;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;
using StickyTodo.Weather;

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

        // 下拉要贴着输入框
        CityPopup.PlacementTarget = NewCityBox;

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

    private void AddCity_Click(object? sender, RoutedEventArgs e) => AddCurrentCity();

    private void City_Click(object? sender, RoutedEventArgs e)    {
        if (sender is Control { Tag: WeatherCityRow city })
        {
            _viewModel.SelectCity(city.Name);
        }
    }

    /// <summary>输入城市名后按回车 = 点「添加」；上下键选候选。</summary>
    private void NewCity_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Avalonia.Input.Key.Down when CityPopup.IsOpen && CitySuggestionList.ItemCount > 0:
                MoveSuggestion(1);
                e.Handled = true;
                break;

            case Avalonia.Input.Key.Up when CityPopup.IsOpen && CitySuggestionList.ItemCount > 0:
                MoveSuggestion(-1);
                e.Handled = true;
                break;

            case Avalonia.Input.Key.Escape:
                CityPopup.IsOpen = false;
                e.Handled = true;
                break;

            case Avalonia.Input.Key.Enter:
                AddCurrentCity();
                e.Handled = true;
                break;
        }
    }

    /// <summary>键盘上下移动候选时不要触发"选中即添加"。</summary>
    private bool _navigatingSuggestions;

    private void MoveSuggestion(int delta)
    {
        _navigatingSuggestions = true;
        try
        {
            CitySuggestionList.SelectedIndex = Math.Clamp(
                CitySuggestionList.SelectedIndex + delta, 0, CitySuggestionList.ItemCount - 1);
        }
        finally
        {
            _navigatingSuggestions = false;
        }
    }

    /// <summary>边输边给建议：全国省市区县表里模糊匹配。</summary>
    private void NewCity_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var items = _viewModel.SuggestCities(NewCityBox.Text ?? string.Empty);
        CitySuggestionList.ItemsSource = items;
        CitySuggestionList.SelectedIndex = -1;
        CityPopup.IsOpen = items.Count > 0;
    }

    /// <summary>点（或键盘选中）候选项 = 直接添加。</summary>
    private void CitySuggestion_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_navigatingSuggestions)
        {
            return;
        }

        if (CitySuggestionList.SelectedItem is DivisionSuggestion)
        {
            AddCurrentCity();
        }
    }

    private void AddCurrentCity()
    {
        var name = CitySuggestionList.SelectedItem is DivisionSuggestion suggestion
            ? suggestion.Name
            : NewCityBox.Text;

        if (_viewModel.AddCity(name ?? string.Empty))
        {
            _viewModel.NewCityName = string.Empty;
            CityPopup.IsOpen = false;
        }

        NewCityBox.Focus();
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
