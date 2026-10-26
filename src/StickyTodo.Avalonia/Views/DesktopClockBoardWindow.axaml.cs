using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>桌面悬浮时钟：小窗、置顶、不进任务栏，悬浮在桌面右下角。</summary>
public partial class DesktopClockBoardWindow : Window
{
    private readonly DesktopClockViewModel _viewModel;

    public DesktopClockBoardWindow(DesktopClockViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // 首次布局后挪到右下角
        Opened += (_, _) => MoveToBottomRight();

        Closed += (_, _) => viewModel.Stop();
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    private void MoveToBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scale = screen?.Scaling ?? 1;
        var area = screen?.WorkingArea;
        if (area is { } work)
        {
            Position = new Avalonia.PixelPoint(
                (int)Math.Round((work.X + work.Width - Bounds.Width * scale - 24)),
                (int)Math.Round((work.Y + work.Height - Bounds.Height * scale - 24)));
        }
    }

    private void Root_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
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

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
