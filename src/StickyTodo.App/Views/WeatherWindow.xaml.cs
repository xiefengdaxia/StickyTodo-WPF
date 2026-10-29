using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StickyTodo.App.ViewModels;
using StickyTodo.Weather;

namespace StickyTodo.App.Views;

/// <summary>天气看板窗口。</summary>
public partial class WeatherWindow : Window
{
    private readonly WeatherViewModel _viewModel;

    public WeatherWindow(WeatherViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        if (viewModel.Cities.Count > 0)
        {
            // 打开时选中默认城市（没设过就是第一个）
            viewModel.SelectDefaultCity();
        }

        Loaded += async (_, _) =>
        {
            // 小屏 / 高缩放下把窗口收进工作区，否则标题栏被顶到屏幕外（看着像"拖不动"）
            Services.ScreenHelper.FitIntoWorkArea(this);
            await viewModel.LoadAsync();
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // 点在按钮上不拖动
        if (e.OriginalSource is DependencyObject source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is System.Windows.Controls.Button)
                {
                    return;
                }

                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
        }

        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void City_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WeatherCityViewModel city })
        {
            _viewModel.SelectCity(city.Name);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _viewModel.LoadAsync(force: true);

    private void AddCity_Click(object sender, RoutedEventArgs e) => AddCurrentCity();

    /// <summary>把输入框里的城市加进列表（下拉里选中了就用选中那条）。</summary>
    private void AddCurrentCity()
    {
        var name = CitySuggestionList.SelectedItem is DivisionSuggestion suggestion
            ? suggestion.Name
            : NewCityBox.Text;

        if (_viewModel.AddCity(name))
        {
            NewCityBox.Clear();
            CityPopup.IsOpen = false;
        }

        NewCityBox.Focus();
    }

    /// <summary>边输边给建议：全国省市区县表里模糊匹配。</summary>
    private void NewCity_TextChanged(object sender, TextChangedEventArgs e)
    {
        var items = _viewModel.SuggestCities(NewCityBox.Text);
        CitySuggestionList.ItemsSource = items;
        CitySuggestionList.SelectedIndex = -1;
        CityPopup.IsOpen = items.Count > 0;
    }

    private void NewCity_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when CityPopup.IsOpen && CitySuggestionList.Items.Count > 0:
                CitySuggestionList.SelectedIndex =
                    Math.Min(CitySuggestionList.SelectedIndex + 1, CitySuggestionList.Items.Count - 1);
                e.Handled = true;
                break;

            case Key.Up when CityPopup.IsOpen && CitySuggestionList.Items.Count > 0:
                CitySuggestionList.SelectedIndex = Math.Max(CitySuggestionList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;

            case Key.Escape:
                CityPopup.IsOpen = false;
                e.Handled = true;
                break;

            case Key.Enter:
                AddCurrentCity();
                e.Handled = true;
                break;
        }
    }

    private void CitySuggestion_Click(object sender, MouseButtonEventArgs e)
    {
        if (CitySuggestionList.SelectedItem is not DivisionSuggestion suggestion)
        {
            return;
        }

        NewCityBox.Text = suggestion.Name;
        AddCurrentCity();
    }

    private void RemoveCity_Click(object sender, RoutedEventArgs e) => _viewModel.RemoveSelectedCity();

    private void SetDefaultCity_Click(object sender, RoutedEventArgs e) => _viewModel.SetDefaultCity();

    /// <summary>展开/收起城市管理行（输入框、添加、设为默认、移除）。</summary>
    private void ToggleCityEditor_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleCityEditor();

        if (_viewModel.IsCityEditorOpen)
        {
            // 展开后光标直接落在输入框，省一次点击
            NewCityBox.Focus();
        }
        else
        {
            CityPopup.IsOpen = false;
        }
    }


    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        // 与其它看板共用外观菜单（皮肤 + 背景透明度 + 整窗透明度）
        if (Application.Current is App app)
        {
            app.ShowWeatherAppearanceMenu(this, sender as FrameworkElement);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
