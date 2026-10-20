using System.Globalization;
using System.Windows.Data;

namespace StickyTodo.App.Converters;

/// <summary>布尔取反，用于「锁定时禁用内容区」这类绑定。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;
}