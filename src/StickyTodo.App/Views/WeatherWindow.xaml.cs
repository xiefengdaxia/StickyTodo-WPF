using System.Windows;
using System.Windows.Input;
using StickyTodo.App.ViewModels;

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
            viewModel.SelectedCity = viewModel.Cities[0];
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

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _viewModel.LoadAsync();

    private void AddCity_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AddCity(NewCityBox.Text))
        {
            NewCityBox.Clear();
        }

        NewCityBox.Focus();
    }

    private void RemoveCity_Click(object sender, RoutedEventArgs e) => _viewModel.RemoveSelectedCity();

    private void NewCity_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddCity_Click(sender, e);
            e.Handled = true;
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
