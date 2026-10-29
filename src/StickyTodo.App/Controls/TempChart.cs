using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using StickyTodo.App.ViewModels;

namespace StickyTodo.App.Controls;

/// <summary>
/// 7 天高低温曲线：高温一条橙线、低温一条蓝线，每个点带圆点和数值标签。
/// 列中心的位置和上面/下面的 <c>UniformGrid Columns="7"</c> 对齐，
/// 所以三条内容（星期、曲线、风况）能形成一列一列的纵向关系。
/// </summary>
public sealed class TempChart : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days),
        typeof(IReadOnlyList<WeatherDayRow>),
        typeof(TempChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDaysChanged));

    public TempChart()
    {
        MinHeight = 96;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<WeatherDayRow>? Days
    {
        get => (IReadOnlyList<WeatherDayRow>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    /// <summary>高温线颜色（默认橙）。</summary>
    public Brush HighBrush { get; set; } = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)));

    /// <summary>低温线颜色（默认蓝）。</summary>
    public Brush LowBrush { get; set; } = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB)));

    /// <summary>数值标签颜色。</summary>
    public Brush LabelBrush { get; set; } = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x80, 0x74)));

    /// <summary>上下给数值标签留的高度。</summary>
    private const double PadY = 22;

    private static void OnDaysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TempChart)d;

        // Days 是 ObservableCollection，界面上是 Clear + Add 原地更新：
        // 订阅集合变化，否则分数线不会跟着刷新。
        if (e.OldValue is INotifyCollectionChanged old)
        {
            old.CollectionChanged -= chart.OnDaysCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged current)
        {
            current.CollectionChanged += chart.OnDaysCollectionChanged;
        }

        chart.InvalidateVisual();
    }

    private void OnDaysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var days = Days;
        if (days is null || days.Count == 0)
        {
            return;
        }

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var slot = width / days.Count;
        var plotHeight = Math.Max(1, height - PadY * 2);

        // 用「当天最高温」和「当天最低温」的整体区间，上下各留 10% 余量，
        // 两条线就不会贴到控件边缘
        var max = days.Max(d => d.TempMax);
        var min = days.Min(d => d.TempMin);
        var range = Math.Max(4, max - min);
        var topValue = max + range * 0.10;
        var bottomValue = min - range * 0.10;
        var span = Math.Max(1, topValue - bottomValue);

        double X(int index) => slot * index + (slot / 2);
        double Y(double value) => PadY + ((topValue - value) / span * plotHeight);

        DrawSeries(
            dc,
            days.Select(d => d.TempMax).ToArray(),
            days.Select(d => d.TempMaxText).ToArray(),
            HighBrush,
            X,
            Y,
            labelAbove: true,
            pad: PadY);

        DrawSeries(
            dc,
            days.Select(d => d.TempMin).ToArray(),
            days.Select(d => d.TempMinText).ToArray(),
            LowBrush,
            X,
            Y,
            labelAbove: false,
            pad: PadY);
    }

    private void DrawSeries(
        DrawingContext dc,
        IReadOnlyList<double> values,
        IReadOnlyList<string> labels,
        Brush brush,
        Func<int, double> x,
        Func<double, double> y,
        bool labelAbove,
        double pad)
    {
        var pen = new Pen(brush, 1.6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();

        // 折线
        if (values.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var i = 0; i < values.Count; i++)
                {
                    var point = new Point(x(i), y(values[i]));
                    if (i == 0)
                    {
                        ctx.BeginFigure(point, isFilled: false, isClosed: false);
                    }
                    else
                    {
                        ctx.LineTo(point, isStroked: true, isSmoothJoin: false);
                    }
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        // 圆点 + 数值
        for (var i = 0; i < values.Count; i++)
        {
            var cx = x(i);
            var cy = y(values[i]);

            // 白心圆点，和折线同色描边：小尺寸下比实心点更清楚
            dc.DrawEllipse(Brushes.White, pen, new Point(cx, cy), 3.2, 3.2);

            var text = labels[i];
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                11,
                brush,
                1.0);

            var left = Math.Min(Math.Max(cx - (formatted.Width / 2), 0), ActualWidth - formatted.Width);
            var top = labelAbove ? cy - formatted.Height - 6 : cy + 6;

            // 别画到控件外面（第一/最后一个点的标签容易被裁掉）
            top = Math.Min(Math.Max(top, 0), ActualHeight - formatted.Height);

            dc.DrawText(formatted, new Point(left, top));
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}