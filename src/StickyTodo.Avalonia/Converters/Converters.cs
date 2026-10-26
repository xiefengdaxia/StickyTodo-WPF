using System.Globalization;
using Avalonia.Data.Converters;
using StickyTodo.Core.Localization;

namespace StickyTodo.Notes.Converters;

/// <summary>bool → 是否可见。null 当作 false。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true;
}

/// <summary>bool → 取反的是否可见。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static readonly InverseBoolToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>bool 取反。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>null / 空串 → 折叠；否则可见。</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value is not string { Length: 0 };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>语言下拉框的显示名（原生名称，例如「日本語」）。</summary>
public sealed class LanguageNameConverter : IValueConverter
{
    public static readonly LanguageNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LanguageInfo info ? info.NativeName : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>年份下拉的显示文本（按语言加「年 / 년 / 」后缀）。</summary>
public sealed class LocalizedYearConverter : IValueConverter
{
    public static readonly LocalizedYearConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int year ? Loc.F("cal.yearFormat", year) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>月份下拉的显示文本。</summary>
public sealed class LocalizedMonthConverter : IValueConverter
{
    public static readonly LocalizedMonthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int month ? Loc.F("cal.monthFormat", month) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>把 0–1 的比例换成百分比文本（趋势图/占比用）。</summary>
public sealed class RatioToPercentConverter : IValueConverter
{
    public static readonly RatioToPercentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double ratio ? $"{ratio * 100:0.#}%" : "0%";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>把 <c>#RRGGBB</c> / <c>#AARRGGBB</c> 颜色串变成画刷（标签胶囊用）。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return Avalonia.Media.Brushes.Gray;
        }

        try
        {
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(text));
        }
        catch (Exception)
        {
            return Avalonia.Media.Brushes.Gray;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 番茄钟进度环：把 0–1 的进度换算成 StrokeDashArray。
/// ConverterParameter 是圆周长（逻辑像素），默认按 160 直径算。
/// </summary>
public sealed class ProgressToDashArrayConverter : IValueConverter
{
    public static readonly ProgressToDashArrayConverter Instance = new();

    private const double DefaultCircumference = 474.4;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = value switch
        {
            double d => Math.Clamp(d, 0, 1),
            _ => 0d,
        };

        var total = DefaultCircumference;
        if (parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            total = parsed;
        }

        var done = progress * total;
        return new Avalonia.Collections.AvaloniaList<double> { done, Math.Max(0.0001, total - done) };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
