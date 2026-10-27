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

        // 小屏 / 高缩放下收进工作区，避免标题栏跑到屏幕外
        Loaded += (_, _) => Services.ScreenHelper.FitIntoWorkArea(this);
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

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.ShowWeatherAppearanceMenu(this, sender as FrameworkElement);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
