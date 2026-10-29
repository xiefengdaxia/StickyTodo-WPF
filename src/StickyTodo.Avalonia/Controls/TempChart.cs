using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Controls;

/// <summary>
/// 7 天高低温曲线：高温一条橙线、低温一条蓝线，每个点带圆点和数值标签。
/// 列中心的位置和上面/下面的 <c>UniformGrid Columns="7"</c> 对齐，
/// 所以三条内容（星期、曲线、风况）能形成一列一列的纵向关系。
/// </summary>
public sealed class TempChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<WeatherDayRow>?> DaysProperty =
        AvaloniaProperty.Register<TempChart, IReadOnlyList<WeatherDayRow>?>(nameof(Days));

    public TempChart()
    {
        MinHeight = 96;
        ClipToBounds = true;
    }

    public IReadOnlyList<WeatherDayRow>? Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    /// <summary>高温线颜色（默认橙）。</summary>
    public IBrush HighBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));

    /// <summary>低温线颜色（默认蓝）。</summary>
    public IBrush LowBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB));

    /// <summary>数值标签颜色。</summary>
    public IBrush LabelBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x8A, 0x80, 0x74));

    /// <summary>上下给数值标签留的高度。</summary>
    private const double PadY = 22;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property != DaysProperty)
        {
            return;
        }

        // Days 是 ObservableCollection，界面上是 Clear + Add 原地更新：
        // 订阅集合变化，否则分数线不会跟着刷新。
        if (e.OldValue is INotifyCollectionChanged old)
        {
            old.CollectionChanged -= OnDaysCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged current)
        {
            current.CollectionChanged += OnDaysCollectionChanged;
        }

        InvalidateVisual();
    }

    private void OnDaysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var days = Days;
        if (days is null || days.Count == 0)
        {
            return;
        }

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var slot = width / days.Count;
        var plotHeight = Math.Max(1, height - PadY * 2);

        // 用整体温区 + 上下各 10% 余量，两条线不会贴到控件边缘
        var max = days.Max(d => d.TempMax);
        var min = days.Min(d => d.TempMin);
        var range = Math.Max(4, max - min);
        var topValue = max + range * 0.10;
        var bottomValue = min - range * 0.10;
        var span = Math.Max(1, topValue - bottomValue);

        double X(int index) => slot * index + (slot / 2);
        double Y(double value) => PadY + ((topValue - value) / span * plotHeight);

        DrawSeries(
            context,
            days.Select(d => d.TempMax).ToArray(),
            days.Select(d => d.TempMaxText).ToArray(),
            HighBrush,
            X,
            Y,
            labelAbove: true);

        DrawSeries(
            context,
            days.Select(d => d.TempMin).ToArray(),
            days.Select(d => d.TempMinText).ToArray(),
            LowBrush,
            X,
            Y,
            labelAbove: false);
    }

    private void DrawSeries(
        DrawingContext context,
        IReadOnlyList<double> values,
        IReadOnlyList<string> labels,
        IBrush brush,
        Func<int, double> x,
        Func<double, double> y,
        bool labelAbove)
    {
        var pen = new Pen(brush, 1.6)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round,
        };

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
                        ctx.BeginFigure(point, isFilled: false);
                    }
                    else
                    {
                        ctx.LineTo(point);
                    }
                }

                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        for (var i = 0; i < values.Count; i++)
        {
            var cx = x(i);
            var cy = y(values[i]);

            // 白心圆点，和折线同色描边：小尺寸下比实心点更清楚
            context.DrawEllipse(Brushes.White, pen, new Point(cx, cy), 3.2, 3.2);

            var text = labels[i];
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI, Segoe UI"),
                11,
                brush);

            var left = Math.Min(Math.Max(cx - (formatted.Width / 2), 0), Math.Max(0, Bounds.Width - formatted.Width));
            var top = labelAbove ? cy - formatted.Height - 6 : cy + 6;
            top = Math.Min(Math.Max(top, 0), Math.Max(0, Bounds.Height - formatted.Height));

            context.DrawText(formatted, new Point(left, top));
        }
    }
}