using System.Globalization;
using System.Windows.Data;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Converters;

/// <summary>
/// 把一个固定 key 的界面文案取出来（XAML 里写不下的动态文本用这个）。
/// 用法：<c>Text="{Binding Converter={StaticResource Loc}, ConverterParameter=settings.title}"</c>。
///
/// 关键点：它**订阅**了 <see cref="Translator.LanguageChanged"/>。
/// 语言切换时，WPF 不会自动重跑转换器，所以这里用 <c>BindingExpression.UpdateTarget</c>
/// 主动刷新——否则切完语言要重启程序才生效。
/// </summary>
public sealed class LocalizedStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string key ? Loc.T(key) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 取文案并格式化：ConverterParameter 是 key，值作为第一个参数。
/// 用法：<c>Text="{Binding Count, Converter={StaticResource LocFormat}, ConverterParameter=board.itemCount}"</c>
/// 多参数时用逗号分隔：key,arg1,arg2（多余的会依次填入 {1} {2}）。
/// </summary>
public sealed class LocalizedFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string spec)
        {
            return string.Empty;
        }

        var parts = spec.Split(',');
        var key = parts[0];

        var args = new List<object?>();

        // 值本身作为 {0}；ConverterParameter 里 key 之后的部分作为后续参数
        if (value is not null and not string { Length: 0 })
        {
            args.Add(value);
        }

        for (var i = 1; i < parts.Length; i++)
        {
            args.Add(parts[i]);
        }

        return Loc.F(key, args.ToArray());
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 语言下拉框的显示名（原生名称，例如「日本語」）。
/// </summary>
public sealed class LanguageNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LanguageInfo info ? info.NativeName : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
/// <summary>年份下拉的显示文本（按语言加「年 / 년 / 」后缀）。</summary>
public sealed class LocalizedYearConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int year ? Loc.F("cal.yearFormat", year) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>月份下拉的显示文本。</summary>
public sealed class LocalizedMonthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int month ? Loc.F("cal.monthFormat", month) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}