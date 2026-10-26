using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;


namespace StickyTodo.App.Views;

/// <summary>桌面悬浮时钟窗口：阳历 + 农历 + 时间，天气就绪时轮播天气。</summary>
public partial class DesktopClockWindow : Window
{
    private readonly DesktopClockViewModel _viewModel;

    public DesktopClockWindow(DesktopClockViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) => MoveToBottomRight();
        Loaded += (_, _) => viewModel.Start();
        Closed += (_, _) => viewModel.Stop();
    }

    private void MoveToBottomRight()
    {
        var screen = System.Windows.SystemParameters.WorkArea;
        Left = screen.Right - ActualWidth - 24;
        Top = screen.Bottom - ActualHeight - 24;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
