using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Effects;

namespace StickyTodo.App.Converters;

/// <summary>
/// 纸面很透的时候不要再画投影。
///
/// 原因：<see cref="DropShadowEffect"/> 是拿卡片自身的像素去算阴影的，
/// 卡片本身半透明（比如背景透明度 10%）时，阴影会从卡片后面透出来，
/// 看起来就是「虚影 / 重影 + 一层底色」。100% 不透明时阴影被挡住，所以看不出来。
/// </summary>
public sealed class ShadowWhenOpaqueConverter : IValueConverter
{
    /// <summary>低于这个不透明度就不画投影。</summary>
    public const double Threshold = 0.85;

    private static readonly DropShadowEffect PaperShadow = CreateShadow();

    private static DropShadowEffect CreateShadow()
    {
        var shadow = new DropShadowEffect
        {
            BlurRadius = 18,
            ShadowDepth = 3,
            Opacity = 0.30,
            Color = System.Windows.Media.Colors.Black,
            Direction = 270,
        };
        shadow.Freeze();
        return shadow;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var opacity = value is double d ? d : 1.0;
        return opacity >= Threshold ? PaperShadow : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
