using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Controls;

/// <summary>
/// 用量趋势图：堆叠柱（输入/输出/推理/缓存读写）+ 折线（调用次数）。
/// 自己 OnRender 画，点击/拖动可以选中某个点，交给上层显示明细。
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series),
        typeof(IReadOnlyList<TokenSeriesPoint>),
        typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex),
        typeof(int),
        typeof(TrendChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent),
        typeof(Brush),
        typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public TrendChart()
    {
        MinHeight = 120;
        ClipToBounds = true;
        IsHitTestVisible = true;
    }

    public IReadOnlyList<TokenSeriesPoint>? Series
    {
        get => (IReadOnlyList<TokenSeriesPoint>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>用户点了某个数据点（-1 表示点了空白取消选择）。</summary>
    public event EventHandler<int>? PointPicked;

    private const double LeftPad = 6;
    private const double RightPad = 6;
    private const double TopPad = 8;
    private const double BottomPad = 16;

    private static readonly Brush InputBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
    private static readonly Brush OutputBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF)));
    private static readonly Brush ReasoningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)));
    private static readonly Brush CacheReadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly Brush CacheWriteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var series = Series;
        if (series is null || series.Count == 0)
        {
            DrawText(dc, Loc.T("token.noData"), width / 2, height / 2, 11, Brushes.Gray, center: true);
            return;
        }

        var plotWidth = Math.Max(1, width - LeftPad - RightPad);
        var plotHeight = Math.Max(1, height - TopPad - BottomPad);
        var baseline = TopPad + plotHeight;

        var maxTotal = Math.Max(1, series.Max(p => p.Total));
        var maxSteps = Math.Max(1, series.Max(p => p.Steps));
        var slot = plotWidth / series.Count;

        // 网格线
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)), 1);
        gridPen.Freeze();
        for (var i = 0; i <= 2; i++)
        {
            var y = TopPad + (plotHeight * i / 2.0);
            dc.DrawLine(gridPen, new Point(LeftPad, y), new Point(width - RightPad, y));
        }

        // 堆叠柱
        for (var i = 0; i < series.Count; i++)
        {
            var point = series[i];
            var x = LeftPad + (slot * i);
            var barWidth = Math.Max(1.5, slot * 0.62);
            var offset = (slot - barWidth) / 2;
            var acc = 0.0;

            void Segment(long value, Brush brush)
            {
                if (value <= 0)
                {
                    return;
                }

                var h = plotHeight * value / (double)maxTotal;
                var top = baseline - acc - h;
                dc.DrawRectangle(brush, null, new Rect(x + offset, top, barWidth, Math.Max(h, 0.8)));
                acc += h;
            }

            Segment(point.Usage.Input, InputBrush);
            Segment(point.Usage.Output, OutputBrush);
            Segment(point.Usage.Reasoning, ReasoningBrush);
            Segment(point.Usage.CacheRead, CacheReadBrush);
            Segment(point.Usage.CacheWrite, CacheWriteBrush);
        }

        // 折线（调用次数）
        if (series.Count > 1)
        {
            var linePen = new Pen(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), 1.6)
            {
                LineJoin = PenLineJoin.Round,
            };
            linePen.Freeze();

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var i = 0; i < series.Count; i++)
                {
                    var cx = LeftPad + (slot * i) + (slot / 2);
                    var cy = baseline - (plotHeight * series[i].Steps / (double)maxSteps);
                    if (i == 0)
                    {
                        ctx.BeginFigure(new Point(cx, cy), false, false);
                    }
                    else
                    {
                        ctx.LineTo(new Point(cx, cy), true, false);
                    }
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, linePen, geometry);
        }

        // 选中竖线
        if (SelectedIndex >= 0 && SelectedIndex < series.Count)
        {
            var cx = LeftPad + (slot * SelectedIndex) + (slot / 2);
            var pen = new Pen(Accent, 1.2) { DashStyle = new DashStyle([3, 2], 0) };
            pen.Freeze();
            dc.DrawLine(pen, new Point(cx, TopPad), new Point(cx, baseline));

            var dot = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            dot.Freeze();
            dc.DrawEllipse(dot, null, new Point(cx, TopPad + 4), 3, 3);
        }

        // 首/中/尾刻度
        DrawTick(dc, series, 0, LeftPad, plotWidth, slot, baseline, width);
        if (series.Count > 2)
        {
            DrawTick(dc, series, series.Count / 2, LeftPad, plotWidth, slot, baseline, width);
        }

        DrawTick(dc, series, series.Count - 1, LeftPad, plotWidth, slot, baseline, width);
    }

    private static void DrawTick(
        DrawingContext dc,
        IReadOnlyList<TokenSeriesPoint> series,
        int index,
        double leftPad,
        double plotWidth,
        double slot,
        double baseline,
        double width)
    {
        var point = series[index];
        var label = point.Label;
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        var cx = leftPad + (slot * index) + (slot / 2);
        DrawText(dc, label, Math.Min(Math.Max(cx, 12), width - 12), baseline + 3, 9, Brushes.Gray, center: true);
    }

    private static void DrawText(
        DrawingContext dc,
        string text,
        double x,
        double y,
        double size,
        Brush brush,
        bool center = false)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            size,
            brush,
            1.0);

        var point = center ? new Point(x - (formatted.Width / 2), y) : new Point(x, y);
        dc.DrawText(formatted, point);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            Pick(e.GetPosition(this));
        }
    }

    private void Pick(Point position)
    {
        var series = Series;
        if (series is null || series.Count == 0)
        {
            return;
        }

        var plotWidth = Math.Max(1, ActualWidth - LeftPad - RightPad);
        var slot = plotWidth / series.Count;
        var index = (int)Math.Floor((position.X - LeftPad) / slot);

        if (index < 0 || index >= series.Count)
        {
            SelectedIndex = -1;
            PointPicked?.Invoke(this, -1);
            return;
        }

        SelectedIndex = index;
        PointPicked?.Invoke(this, index);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}