using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using StickyTodo.Core.Entities;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>
/// 性能看板窗口：CPU（折线）/ 内存 / 磁盘 / GPU / 网络。
/// 折线与进度条这种逐帧几何变化放代码后置，绑定不适合高频重算。
/// </summary>
public partial class PerformanceBoardWindow : Window
{
    private readonly PerformanceViewModel _viewModel;
    private readonly WindowGeometryMemory _geometry;

    public PerformanceBoardWindow(PerformanceViewModel viewModel, Board board, WindowManager windows)
    {
        _viewModel = viewModel;
        _geometry = WindowGeometryMemory.Attach(this, board, windows, restoreSize: true);
        InitializeComponent();
        DataContext = viewModel;

        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Opened += (_, _) =>
        {
            if (!_geometry.Restore())
            {
                Services.ScreenHelper.FitIntoWorkArea(this);
            }

            _geometry.Arm();
            _viewModel.Start();
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            DrawCpuChart();
        };

        SizeChanged += (_, _) => DrawCpuChart();
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Stop();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PerformanceViewModel.CpuText))
        {
            DrawCpuChart();
            UpdateBar(MemoryBar, _viewModel.MemoryValue);
            UpdateMemoryColor();
            UpdateBar(DiskBar, _viewModel.DiskActiveValue);
            UpdateBar(GpuBar, _viewModel.GpuValue);
        }
    }

    /// <summary>CPU 折线：历史值画成 Polyline（最新值贴右缘，顶部留 2px 余量）。</summary>
    private void DrawCpuChart()
    {
        CpuChart.Children.Clear();

        // 以窗口客户区宽推算画布宽：窗口 440 DIP - 窗体外边距 20 - 卡片 Margin/Padding/竖条 ≈ 55；
        // 高度固定 50。避免依赖中间容器布局时序。
        var width = ClientSize.Width - 70;
        const double height = 50;

        var history = _viewModel.CpuHistory;
        if (width < 50 || history.Count < 2)
        {
            return;
        }

        var step = width / (PerformanceViewModel.CpuHistoryLength - 1);
        var startX = width - ((history.Count - 1) * step);

        var points = new List<Avalonia.Point>();
        for (var i = 0; i < history.Count; i++)
        {
            var x = startX + (i * step);
            var y = height - ((history[i] / 100.0) * (height - 4)) - 2;
            points.Add(new Avalonia.Point(x, y));
        }

        var accent = PerformanceColors.GetAccentColor();

        // 面积填充：折线以下刷一层淡强调色，比裸线条好看
        var fill = new Avalonia.Controls.Shapes.Polygon
        {
            Fill = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x2E, accent.R, accent.G, accent.B)),
            Points = [.. points, new Avalonia.Point(points[^1].X, height), new Avalonia.Point(points[0].X, height)],
        };

        var polyline = new Avalonia.Controls.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 1.8,
            Points = points,
        };

        CpuChart.Children.Add(fill);
        CpuChart.Children.Add(polyline);
    }

    private void UpdateBar(Border bar, double percent)
    {
        var parent = bar.Parent as Panel;
        var width = parent?.Bounds.Width ?? 0;
        if (width < 1)
        {
            return;
        }

        bar.Width = Math.Max(0, width * Math.Clamp(percent, 0, 100) / 100.0);
    }

    /// <summary>内存 75% 换橙、90% 换红。</summary>
    private void UpdateMemoryColor()
    {
        MemoryBar.Background = _viewModel.MemoryLevel switch
        {
            2 => new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
            1 => new SolidColorBrush(Color.FromRgb(0xE6, 0x8A, 0x2E)),
            _ => new SolidColorBrush(PerformanceColors.GetAccentColor()),
        };
    }

    private void TitleBar_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

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
            var menu = Views.WeatherMenuBuilder.BuildMenu(services.Skin, () =>
            {
                services.Windows.ApplySkin();
            });
            menu.PlacementTarget = this;
            menu.Open(this);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
