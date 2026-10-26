using System.Windows;
using System.Windows.Input;
using StickyTodo.App.ViewModels;

namespace StickyTodo.App.Views;

/// <summary>世界时钟看板窗口。</summary>
public partial class WorldClockWindow : Window
{
    private readonly WorldClockViewModel _viewModel;

    public WorldClockWindow(WorldClockViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.ShowWeatherAppearanceMenu(this, sender as FrameworkElement);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
