using System.Windows;
using System.Windows.Input;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Views;

/// <summary>网速看板窗口：按进程展示实时下载 / 上传，上下两组。</summary>
public partial class NetworkSpeedWindow : Window
{
    private readonly NetworkSpeedViewModel _viewModel;
    private readonly WindowGeometryMemory _geometry;

    public NetworkSpeedWindow(NetworkSpeedViewModel viewModel, Board board, WindowManager windows)
    {
        _viewModel = viewModel;
        _geometry = WindowGeometryMemory.Attach(this, board, windows, restoreSize: true);
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (_, _) => App.ApplyToolWindowOpacity(this);

        Loaded += (_, _) =>
        {
            // 有记录就摆回上次的位置；没记录才居中，并保证不超出工作区
            if (!_geometry.Restore())
            {
                Services.ScreenHelper.FitIntoWorkArea(this);
            }

            _geometry.Arm();
            _viewModel.Start();
        };

        Closed += (_, _) => _viewModel.Stop();

        // 没人在看这块看板时降频采样
        Activated += (_, _) => _viewModel.IsActive = true;
        Deactivated += (_, _) => _viewModel.IsActive = false;
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
