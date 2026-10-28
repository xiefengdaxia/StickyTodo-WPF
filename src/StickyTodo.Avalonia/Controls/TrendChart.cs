using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;

namespace StickyTodo.Notes.Controls;

/// <summary>
/// 用量趋势图：堆叠柱（输入/输出/推理/缓存读写）+ 折线（调用次数）。
/// 自己 Render 画，点击/拖动可以选中某个点，交给上层显示明细。
/// </summary>
public sealed class TrendChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<TokenSeriesPoint>?> SeriesProperty =
        AvaloniaProperty.Register<TrendChart, IReadOnlyList<TokenSeriesPoint>?>(nameof(Series));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<TrendChart, int>(nameof(SelectedIndex), -1);

    public static readonly StyledProperty<IBrush> AccentProperty =
        AvaloniaProperty.Register<TrendChart, IBrush>(nameof(Accent), Brushes.OrangeRed);

    static TrendChart()
    {
        AffectsRender<TrendChart>(SeriesProperty, SelectedIndexProperty, AccentProperty);
        FocusableProperty.OverrideDefaultValue<TrendChart>(false);
    }

    public TrendChart()
    {
        ClipToBounds = true;
        MinHeight = 120;
    }

    public IReadOnlyList<TokenSeriesPoint>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public IBrush Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>用户点了某个数据点（-1 表示点了空白取消选择）。</summary>
    public event EventHandler<int>? PointPicked;

    /// <summary>鼠标悬停到某个数据点上——语义与点击一致，直接显示该点明细。</summary>
    public event EventHandler<int>? PointHovered;

    /// <summary>当前悬停的列（-1 = 指针不在任何有效数据点上）。</summary>
    private int _hoverIndex = -1;

    private const double LeftPad = 6;
    private const double RightPad = 6;
    private const double TopPad = 8;
    private const double BottomPad = 16;

    private static readonly IBrush InputBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly IBrush OutputBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF));
    private static readonly IBrush ReasoningBrush = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));
    private static readonly IBrush CacheReadBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly IBrush CacheWriteBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly IBrush StepsLineBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly IBrush GrayBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)), 1);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var series = Series;
        if (series is null || series.Count == 0)
        {
            DrawText(context, Loc.T("token.noData"), width / 2, height / 2, 11, GrayBrush, center: true);
            return;
        }

        // 整块透明背景：柱子之间有空隙，没有它就只有「画到的地方」能命中指针，
        // 悬停会一格一格地丢事件。
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, width, height));

        var plotWidth = Math.Max(1, width - LeftPad - RightPad);
        var plotHeight = Math.Max(1, height - TopPad - BottomPad);
        var baseline = TopPad + plotHeight;

        var maxTotal = Math.Max(1, series.Max(p => p.Total));
        var maxSteps = Math.Max(1, series.Max(p => p.Steps));
        var slot = plotWidth / series.Count;

        // 网格线
        for (var i = 0; i <= 2; i++)
        {
            var y = TopPad + (plotHeight * i / 2.0);
            context.DrawLine(GridPen, new Point(LeftPad, y), new Point(width - RightPad, y));
        }

        // 悬停高亮：指针所在的那一列（主题色淡淡铺一层，深色皮肤也看得见）
        if (_hoverIndex >= 0 && _hoverIndex < series.Count)
        {
            using (context.PushOpacity(0.14))
            {
                context.FillRectangle(Accent, new Rect(LeftPad + (slot * _hoverIndex), TopPad, slot, plotHeight));
            }
        }

        // 堆叠柱
        for (var i = 0; i < series.Count; i++)
        {
            var point = series[i];
            var x = LeftPad + (slot * i);
            var barWidth = Math.Max(1.5, slot * 0.62);
            var offset = (slot - barWidth) / 2;
            var acc = 0.0;

            void Segment(long value, IBrush brush)
            {
                if (value <= 0)
                {
                    return;
                }

                var h = plotHeight * value / (double)maxTotal;
                var top = baseline - acc - h;
                context.FillRectangle(brush, new Rect(x + offset, top, barWidth, Math.Max(h, 0.8)));
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
            var pen = new Pen(StepsLineBrush, 1.6) { LineJoin = PenLineJoin.Round };
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var i = 0; i < series.Count; i++)
                {
                    var cx = LeftPad + (slot * i) + (slot / 2);
                    var cy = baseline - (plotHeight * series[i].Steps / (double)maxSteps);
                    if (i == 0)
                    {
                        ctx.BeginFigure(new Point(cx, cy), false);
                    }
                    else
                    {
                        ctx.LineTo(new Point(cx, cy));
                    }
                }

                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        // 选中竖线
        if (SelectedIndex >= 0 && SelectedIndex < series.Count)
        {
            var cx = LeftPad + (slot * SelectedIndex) + (slot / 2);
            var pen = new Pen(Accent, 1.2) { DashStyle = new DashStyle([3, 2], 0) };
            context.DrawLine(pen, new Point(cx, TopPad), new Point(cx, baseline));
            context.DrawEllipse(StepsLineBrush, null, new Point(cx, TopPad + 4), 3, 3);
        }

        // 首/中/尾刻度
        DrawTick(context, series, 0, LeftPad, slot, baseline, width);
        if (series.Count > 2)
        {
            DrawTick(context, series, series.Count / 2, LeftPad, slot, baseline, width);
        }

        DrawTick(context, series, series.Count - 1, LeftPad, slot, baseline, width);
    }

    private static void DrawTick(
        DrawingContext context,
        IReadOnlyList<TokenSeriesPoint> series,
        int index,
        double leftPad,
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
        DrawText(context, label, Math.Min(Math.Max(cx, 12), width - 12), baseline + 3, 9, GrayBrush, center: true);
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        double x,
        double y,
        double size,
        IBrush brush,
        bool center = false)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI, Segoe UI"),
            size,
            brush);

        var point = center ? new Point(x - (formatted.Width / 2), y) : new Point(x, y);
        context.DrawText(formatted, point);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Pick(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // 按住拖动 = 连续选择；不按键时 = 悬停，等价于点击
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Pick(e.GetPosition(this));
            return;
        }

        Hover(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverIndex < 0)
        {
            return;
        }

        // 只收起高亮，不动已经显示出来的明细（悬停=点击，点完的结果留着）
        _hoverIndex = -1;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    /// <summary>悬停：指针落在哪一列，就等同于点击那一列。</summary>
    private void Hover(Point position)
    {
        var series = Series;
        if (series is null || series.Count == 0)
        {
            return;
        }

        var index = IndexAt(position.X, series.Count);
        if (index == _hoverIndex)
        {
            return;
        }

        if (index < 0)
        {
            // 落在左右留白上：取消高亮，但保留已有选择（免得指针划出图就把明细清掉）
            _hoverIndex = -1;
            Cursor = Cursor.Default;
            InvalidateVisual();
            return;
        }

        _hoverIndex = index;
        Cursor = new Cursor(StandardCursorType.Hand);
        InvalidateVisual();

        SelectedIndex = index;
        PointHovered?.Invoke(this, index);
    }

    /// <summary>回归用：模拟悬停到第 index 列（按列中点换算 x）。</summary>
    internal void HoverForTest(int index)
    {
        var series = Series;
        if (series is null || series.Count == 0 || index < 0 || index >= series.Count)
        {
            return;
        }

        var plotWidth = Math.Max(1, Bounds.Width - LeftPad - RightPad);
        var slot = plotWidth / series.Count;
        Hover(new Point(LeftPad + (slot * index) + (slot / 2), Bounds.Height / 2));
    }

    private int IndexAt(double x, int count)
    {
        var plotWidth = Math.Max(1, Bounds.Width - LeftPad - RightPad);
        var slot = plotWidth / count;
        var index = (int)Math.Floor((x - LeftPad) / slot);
        return index >= 0 && index < count ? index : -1;
    }

    private void Pick(Point position)
    {
        var series = Series;
        if (series is null || series.Count == 0)
        {
            return;
        }

        var index = IndexAt(position.X, series.Count);

        if (index < 0)
        {
            SelectedIndex = -1;
            PointPicked?.Invoke(this, -1);
            return;
        }

        SelectedIndex = index;
        PointPicked?.Invoke(this, index);
    }
}
