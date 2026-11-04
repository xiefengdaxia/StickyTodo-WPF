using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Views;

/// <summary>
/// 性能看板窗口：CPU（折线）/ 内存 / 磁盘 / GPU / 网络。
/// 折线与进度条这种逐帧变化的部分放代码后置，绑定不适合高频几何重算。
/// </summary>
public partial class PerformanceWindow : Window
{
    private readonly PerformanceViewModel _viewModel;
    private readonly WindowGeometryMemory _geometry;
    private readonly List<double> _cpuHistory = new();

    public PerformanceWindow(PerformanceViewModel viewModel, Board board, WindowManager windows)
    {
        _viewModel = viewModel;
        _geometry = WindowGeometryMemory.Attach(this, board, windows, restoreSize: true);
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (_, _) =>
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

        // 用户没在看这块看板时降频采样（VM 里按 1/3 走）
        Activated += (_, _) => _viewModel.IsActive = true;
        Deactivated += (_, _) => _viewModel.IsActive = false;

        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Stop();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PerformanceViewModel.CpuValue))
        {
            _cpuHistory.Add(_viewModel.CpuValue);
            while (_cpuHistory.Count > PerformanceViewModel.CpuHistoryLength)
            {
                _cpuHistory.RemoveAt(0);
            }

            DrawCpuChart();
            UpdateBar(MemoryBar, _viewModel.MemoryValue);
            UpdateMemoryColor();
            UpdateBar(DiskBar, _viewModel.DiskActiveValue);
            UpdateBar(GpuBar, _viewModel.GpuValue);
        }
    }

    /// <summary>CPU 折线：把历史值画成 CpuChart 里的 Polyline（最新值贴右缘）。</summary>
    private void DrawCpuChart()
    {
        var canvas = CpuChart;
        canvas.Children.Clear();

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 10 || height < 5 || _cpuHistory.Count < 2)
        {
            return;
        }

        // 点距按"可视宽度 / 60 秒"算，但整条曲线右对齐：
        // 历史不足 60 秒时左侧留白，正好表现"采样刚开始"
        var step = width / (PerformanceViewModel.CpuHistoryLength - 1);
        var startX = width - ((_cpuHistory.Count - 1) * step);

        var points = new PointCollection();
        for (var i = 0; i < _cpuHistory.Count; i++)
        {
            var x = startX + (i * step);
            var y = height - ((_cpuHistory[i] / 100.0) * (height - 4)) - 2;
            points.Add(new Point(x, y));
        }

        // 面积填充：折线以下刷一层淡强调色，比裸线条好看
        var fill = new Polygon
        {
            Fill = new SolidColorBrush(((SolidColorBrush)FindResource("Brush.Accent")).Color with { A = 0x2E }),
            Points = [.. points, new Point(points[^1].X, height), new Point(points[0].X, height)],
        };

        var polyline = new Polyline
        {
            Stroke = (Brush)FindResource("Brush.Accent"),
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Points = points,
        };

        canvas.Children.Add(fill);
        canvas.Children.Add(polyline);
    }

    private static void UpdateBar(System.Windows.Controls.Border bar, double percent)
    {
        var parent = bar.Parent as System.Windows.Controls.Grid;
        var width = parent?.ActualWidth ?? 0;
        if (width < 1)
        {
            return;
        }

        bar.Width = Math.Max(0, width * Math.Clamp(percent, 0, 100) / 100.0);
    }

    /// <summary>内存 75% 换橙、90% 换红。</summary>
    private void UpdateMemoryColor()
    {
        var brush = _viewModel.MemoryLevel switch
        {
            2 => new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
            1 => new SolidColorBrush(Color.FromRgb(0xE6, 0x8A, 0x2E)),
            _ => (Brush)FindResource("Brush.Accent"),
        };

        MemoryBar.Background = brush;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

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
