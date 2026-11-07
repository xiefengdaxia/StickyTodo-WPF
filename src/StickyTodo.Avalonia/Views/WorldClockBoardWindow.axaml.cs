using Avalonia.Controls;
using Avalonia.Interactivity;
using StickyTodo.Core.Entities;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>世界时钟看板窗口。不绑定看板数据，走无看板的简化基类。</summary>
public partial class WorldClockBoardWindow : Window
{
    private readonly WorldClockViewModel _viewModel;
    private readonly WindowGeometryMemory _geometry;

    public WorldClockBoardWindow(WorldClockViewModel viewModel, Board board, WindowManager windows)
    {
        _viewModel = viewModel;
        _geometry = WindowGeometryMemory.Attach(this, board, windows, restoreSize: true);
        InitializeComponent();
        DataContext = viewModel;

        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 工具窗自己的整窗透明度（没单独设过就跟随全局）
        Opened += (_, _) =>
        {
            if (App.Services is { } services)
            {
                Services.ToolWindowOpacity.Apply(this, services.Settings);
            }
        };

        Opened += (_, _) =>
        {
            if (!_geometry.Restore())
            {
                Services.ScreenHelper.FitIntoWorkArea(this);
            }

            _geometry.Arm();
            _viewModel.Start();
        };

        Closed += (_, _) => _viewModel.Stop();
    }

    private void TitleBar_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 点在按钮上不触发拖动
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

    private void Appearance_Click(object? sender, RoutedEventArgs e)
    {
        if (App.Services is { } services)
        {
            var menu = Views.WeatherMenuBuilder.BuildMenu(
                services.Settings,
                services.Skin,
                this,
                () =>
                {
                    // 改动后刷新所有窗口资源
                    services.Windows.ApplySkin();
                });
            menu.PlacementTarget = this;
            menu.Open(this);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
