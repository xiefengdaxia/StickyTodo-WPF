using System.Windows;
using StickyTodo.App.Views;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Services;

/// <summary>
/// 工具窗（天气 / 世界时钟 / 网速 / 性能 / 桌面时钟）的整窗透明度。
///
/// 这些窗口没有看板 id，按「窗口类型」存一个 key，所以同类窗口只有一个设置值。
/// </summary>
public static class ToolWindowAppearance
{
    /// <summary>窗口用哪个 key 存透明度。</summary>
    public static string KeyFor(Window window) => window switch
    {
        WeatherWindow => "tool.weather",
        WorldClockWindow => "tool.worldclock",
        DesktopClockWindow => "tool.desktopclock",
        NetworkSpeedWindow => "tool.networkspeed",
        PerformanceWindow => "tool.performance",
        _ => "tool." + window.GetType().Name,
    };

    /// <summary>按存下来的值给窗口设透明度（没有单独设置就跟随全局）。</summary>
    public static void Apply(Window window, AppSettingsService settings) =>
        window.Opacity = settings.GetWindowOpacity(KeyFor(window));

    /// <summary>修改并立即应用。</summary>
    public static void Set(Window window, AppSettingsService settings, double value)
    {
        settings.SetWindowOpacity(KeyFor(window), value);
        window.Opacity = settings.GetWindowOpacity(KeyFor(window));
    }

    /// <summary>取消单独设置，回到跟随全局。</summary>
    public static void Reset(Window window, AppSettingsService settings)
    {
        settings.ClearWindowOpacity(KeyFor(window));
        window.Opacity = settings.GetWindowOpacity(KeyFor(window));
    }
}
