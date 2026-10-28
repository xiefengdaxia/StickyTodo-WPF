using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Controls;

/// <summary>
/// Token 热力图：GitHub contributions 风格的「周 × 天」格子。
/// 每列一周、每行周一~周日；颜色深浅按当天用量占总量的比例。
/// 数据源是 TokenStats.Series（天粒度），由调用方保证 granularity == "day"。
/// 悬停显示当天用量；点击触发明细（与趋势图一致的交互）。
/// </summary>
public sealed class HeatMap : Canvas
{
    public event EventHandler<int>? DayPicked;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<TokenSeriesPoint>), typeof(HeatMap),
        new PropertyMetadata(null, OnSeriesChanged));

    public IReadOnlyList<TokenSeriesPoint>? Series
    {
        get => (IReadOnlyList<TokenSeriesPoint>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(HeatMap), new PropertyMetadata(Brushes.Goldenrod));

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HeatMap)d).Rebuild();

    private const double Cell = 15d;
    private const double Gap = 3d;
    private const double Side = 24d;   // 左侧星期标签宽
    private const double Top = 2d;

    /// <summary>格子命中区（绘制顺序与 Series 索引一一对应；-1 = 空格子）。</summary>
    private readonly List<(Rect Rect, int SeriesIndex)> _hits = new();

    public HeatMap()
    {
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => ToolTip = null;
        MouseLeftButtonDown += OnClick;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        foreach (var (rect, index) in _hits)
        {
            if (index >= 0 && rect.Contains(pos))
            {
                var point = Series![index];
                ToolTip = $"{point.Key}  {point.Total:N0} tokens · {point.Steps} 次";
                return;
            }
        }

        ToolTip = null;
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        foreach (var (rect, index) in _hits)
        {
            if (index >= 0 && rect.Contains(pos))
            {
                DayPicked?.Invoke(this, index);
                return;
            }
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        _hits.Clear();

        var series = Series;
        if (series is null || series.Count == 0)
        {
            Width = Side;
            Height = Top + 7 * (Cell + Gap);
            return;
        }

        var max = 0L;
        foreach (var p in series)
        {
            if (p.Total > max)
            {
                max = p.Total;
            }
        }

        // 颜色分 5 档：0 档是底色，1~4 档按主题色加深
        var levelBrushes = new Brush[5];
        levelBrushes[0] = new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x38, 0x2A));
        if (Accent is SolidColorBrush solid)
        {
            var c = solid.Color;
            for (var level = 1; level <= 4; level++)
            {
                var alpha = (byte)(70 + level * 55); // 125 / 180 / 235 / 255+
                levelBrushes[level] = new SolidColorBrush(Color.FromArgb(Math.Min(alpha, (byte)255), c.R, c.G, c.B));
            }
        }
        else
        {
            levelBrushes[1] = Brushes.Gray;
            levelBrushes[2] = Brushes.DarkGray;
            levelBrushes[3] = Brushes.DimGray;
            levelBrushes[4] = Brushes.Black;
        }

        // 第一天补到正确的星期行：第一列只画第一周的部分
        var first = DateOnly.ParseExact(series[0].Key, "yyyy-MM-dd");
        var firstDow = IsoDayOfWeek(first); // 1=周一 … 7=周日

        var col = 0;
        var row = firstDow;

        for (var index = 0; index < series.Count; index++)
        {
            var point = series[index];
            if (!DateOnly.TryParseExact(point.Key, "yyyy-MM-dd", out var day))
            {
                continue;
            }

            // 期望行列推进：从 (col,row) 走到这天的位置——中间的空日期补空格子
            while (row < IsoDayOfWeek(day))
            {
                _hits.Add((new Rect(Side + col * (Cell + Gap), Top + (row - 1) * (Cell + Gap), Cell, Cell), -1));
                row++;
            }

            if (row == 8)
            {
                // 跨周：换列、回到周一
                col++;
                row = 1;
            }

            var level = point.Total <= 0 ? 0 : Math.Min(4, 1 + (int)(point.Total * 4.0 / Math.Max(1, max)));
            var cell = new System.Windows.Shapes.Rectangle
            {
                Width = Cell,
                Height = Cell,
                RadiusX = 3,
                RadiusY = 3,
                Fill = levelBrushes[level],
                Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                StrokeThickness = 0.5,
                Tag = index,
            };
            var rect = new Rect(Side + col * (Cell + Gap), Top + (row - 1) * (Cell + Gap), Cell, Cell);
            SetLeft(cell, rect.X);
            SetTop(cell, rect.Y);
            Children.Add(cell);
            _hits.Add((rect, index));

            // 推进到下一天
            row++;
            if (row > 7)
            {
                col++;
                row = 1;
            }
        }

        Width = Side + (col + 1) * (Cell + Gap);
        Height = Top + 7 * (Cell + Gap);

        DrawWeekdayLabels();
    }

    private void DrawWeekdayLabels()
    {
        // 只标一/三/五，避免拥挤
        string[] labels = ["一", "", "三", "", "五", "", "日"];
        for (var row = 0; row < 7; row++)
        {
            if (labels[row].Length == 0)
            {
                continue;
            }

            var text = new TextBlock
            {
                Text = labels[row],
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0x3B, 0x38, 0x2A)),
            };
            SetLeft(text, 0);
            SetTop(text, Top + row * (Cell + Gap) + 2);
            Children.Add(text);
        }
    }

    private static int IsoDayOfWeek(DateOnly day)
        => ((int)day.DayOfWeek + 6) % 7 + 1;
}
