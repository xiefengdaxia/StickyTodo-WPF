using System.Windows;
using System.Windows.Input;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Views;

/// <summary>世界时钟看板窗口。</summary>
public partial class WorldClockWindow : Window
{
    private readonly WorldClockViewModel _viewModel;
    private readonly WindowGeometryMemory _geometry;

    public WorldClockWindow(WorldClockViewModel viewModel, Board board, WindowManager windows)
    {
        _viewModel = viewModel;
        _geometry = WindowGeometryMemory.Attach(this, board, windows, restoreSize: true);
        InitializeComponent();
        DataContext = viewModel;

        // 位置在窗口可见之前就摆好（SourceInitialized 时句柄已建、还没画出来），
        // 放到 Loaded 里会先闪一下默认位置
        SourceInitialized += (_, _) =>
        {
            if (!_geometry.Restore())
            {
                Services.ScreenHelper.FitIntoWorkArea(this);
            }

            _geometry.Arm();
        };

        Loaded += (_, _) => _viewModel.Start();

        Closed += (_, _) => _viewModel.Stop();
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
