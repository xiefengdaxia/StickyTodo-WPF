using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using StickyTodo.Core.Entities;

namespace StickyTodo.Notes.Controls;

/// <summary>
/// Token 热力图：GitHub contributions 风格的「周 × 天」格子。
/// 每列一周、每行周一~周日；颜色深浅按当天用量占总量的比例。
/// 数据源是 TokenStats.Series（天粒度），由调用方保证 granularity == "day"。
/// 悬停显示当天用量；点击触发明细（与趋势图一致的交互）。
/// </summary>
public sealed class HeatMap : Canvas
{
    public static readonly StyledProperty<IReadOnlyList<TokenSeriesPoint>?> SeriesProperty =
        AvaloniaProperty.Register<HeatMap, IReadOnlyList<TokenSeriesPoint>?>(nameof(Series),
            coerce: (_, v) => v, defaultBindingMode: Avalonia.Data.BindingMode.OneWay);

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<HeatMap, IBrush?>(nameof(Accent));

    public IReadOnlyList<TokenSeriesPoint>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private const double Cell = 15d;
    private const double Gap = 3d;
    private const double Side = 24d;
    private const double Top = 2d;

    /// <summary>格子命中区；SeriesIndex = -1 是空格子。</summary>
    private readonly List<(Rect Rect, int SeriesIndex)> _hits = new();

    /// <summary>点击某天：与趋势图同一条明细链路（index 进 ShowPointDetail）。</summary>
    public event EventHandler<int>? DayPicked;

    public HeatMap()
    {
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => ToolTip.SetTip(this, null);
        PointerPressed += OnPointerPressed;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SeriesProperty)
        {
            Rebuild();
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        foreach (var (rect, index) in _hits)
        {
            if (index >= 0 && rect.Contains(pos))
            {
                var point = Series![index];
                ToolTip.SetTip(this, $"{point.Key}  {point.Total:N0} tokens · {point.Steps} 次");
                return;
            }
        }

        ToolTip.SetTip(this, null);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
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

        var levelBrushes = new IBrush[5];
        levelBrushes[0] = new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x38, 0x2A));
        if (Accent is SolidColorBrush solid)
        {
            var c = solid.Color;
            for (var level = 1; level <= 4; level++)
            {
                var alpha = (byte)Math.Min(70 + level * 55, 255);
                levelBrushes[level] = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            }
        }
        else
        {
            levelBrushes[1] = Brushes.Gray;
            levelBrushes[2] = Brushes.DarkGray;
            levelBrushes[3] = Brushes.DimGray;
            levelBrushes[4] = Brushes.Black;
        }

        // 只认天粒度（yyyy-MM-dd）。小时粒度的键是 "MM-dd HH:00"，画热力图没有意义，
        // 也绝不能在这里抛错（会沿绑定链路炸到 UI 线程）——直接不画。
        if (!DateOnly.TryParseExact(series[0].Key, "yyyy-MM-dd", out var first))
        {
            Width = Side;
            Height = Top + 7 * (Cell + Gap);
            DrawWeekdayLabels();
            return;
        }

        var col = 0;
        var row = IsoDayOfWeek(first);

        for (var index = 0; index < series.Count; index++)
        {
            var point = series[index];
            if (!DateOnly.TryParseExact(point.Key, "yyyy-MM-dd", out var day))
            {
                continue;
            }

            // 中间的空日期补空格子
            while (row < IsoDayOfWeek(day))
            {
                _hits.Add((new Rect(Side + col * (Cell + Gap), Top + (row - 1) * (Cell + Gap), Cell, Cell), -1));
                row++;
            }

            if (row == 8)
            {
                col++;
                row = 1;
            }

            var level = point.Total <= 0 ? 0 : Math.Min(4, 1 + (int)(point.Total * 4.0 / Math.Max(1, max)));
            var cell = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = Cell,
                Height = Cell,
                RadiusX = 3,
                RadiusY = 3,
                Fill = levelBrushes[level],
                Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                StrokeThickness = 0.5,
            };
            var rect = new Rect(Side + col * (Cell + Gap), Top + (row - 1) * (Cell + Gap), Cell, Cell);
            SetLeft(cell, rect.X);
            SetTop(cell, rect.Y);
            Children.Add(cell);
            _hits.Add((rect, index));

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
