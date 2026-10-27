using Avalonia.Controls;
using Avalonia.Interactivity;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>世界时钟看板窗口。不绑定看板数据，走无看板的简化基类。</summary>
public partial class WorldClockBoardWindow : Window
{
    public WorldClockBoardWindow(WorldClockViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Opened += (_, _) => Services.ScreenHelper.FitIntoWorkArea(this);
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
            var skin = services.Skin;
            var menu = Views.WeatherMenuBuilder.BuildMenu(skin, () =>
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
