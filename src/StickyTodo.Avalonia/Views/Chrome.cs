using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StickyTodo.Notes.Views;

/// <summary>
/// 无边框窗口的"外观降级"处理。
///
/// Windows 7 只能用 <c>RedirectionSurface</c> 合成，而它**不支持窗口透明**；
/// 此时如果仍然按透明来画（纸面留白 + 圆角 + 阴影），窗口会整片发黑。
/// 所以在窗口显示后检查 <see cref="TopLevel.ActualTransparencyLevel"/>：
///   * 支持透明 → 保留圆角、阴影、留白；
///   * 不支持   → 换成不透明外观（直角、无阴影、窗口底色用纸色），
///               同时设置 <see cref="TopLevel.TransparencyBackgroundFallback"/> 兜底。
/// </summary>
internal static class Chrome
{
    private const string Shadow = "0 4 24 0 #4D000000";

    /// <summary>按当前平台的透明能力，给窗口外壳套上合适的外观。</summary>
    public static void Apply(Window window, Border shell, double radius = 12, double margin = 10)
    {
        var transparent = window.ActualTransparencyLevel != WindowTransparencyLevel.None;
        var skin = App.Services?.Skin.Current;
        IBrush opaque = skin is null
            ? Brushes.Transparent
            : new SolidColorBrush(skin.BackgroundBottom);

        // 透明不可用时的兜底底色（Avalonia 官方给的口子）
        window.TransparencyBackgroundFallback = opaque;

        if (transparent)
        {
            shell.CornerRadius = new CornerRadius(radius);
            shell.Margin = new Thickness(margin);
            shell.BoxShadow = BoxShadows.Parse(Shadow);
            return;
        }

        shell.CornerRadius = new CornerRadius(0);
        shell.Margin = new Thickness(0);
        shell.BoxShadow = default;
        shell.BorderThickness = default;
        window.Background = opaque;
    }
}
