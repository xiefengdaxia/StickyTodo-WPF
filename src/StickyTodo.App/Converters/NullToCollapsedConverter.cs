using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace StickyTodo.App.Converters;

/// <summary>字符串为空/null → Collapsed，否则 Visible。</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}