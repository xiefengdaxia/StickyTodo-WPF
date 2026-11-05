using System.Windows;
using System.Windows.Controls;
using StickyTodo.App.Services;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Views;

/// <summary>
/// 天气 / 世界时钟窗口共用的外观菜单。
/// 这两个窗口不实现 IAppearanceHost（它们不参与窗口间同步），
/// 直接读写 SkinService 并刷新 Application 资源即可。
/// </summary>
public static class WeatherMenuBuilder
{
    public static ContextMenu Build(
        SkinService skin,
        AppSettingsService settings,
        Window? window = null,
        string? windowKey = null)
    {
        var menu = new ContextMenu();

        // 皮肤
        var skinMenu = new MenuItem { Header = "皮肤" };
        foreach (var s in NoteSkins.All)
        {
            var item = new MenuItem
            {
                Header = s.DisplayName,
                IsCheckable = true,
                IsChecked = string.Equals(s.Key, skin.Current.Key, StringComparison.OrdinalIgnoreCase),
            };
            var key = s.Key;
            item.Click += (_, _) => skin.SetSkin(key);
            skinMenu.Items.Add(item);
        }

        menu.Items.Add(skinMenu);

        // 整窗透明度：默认跟随全局，也可以给「这类窗口」单独设一个值
        var currentOpacity = windowKey is null ? settings.WindowOpacity : settings.GetWindowOpacity(windowKey);
        var hasOwn = windowKey is not null && settings.HasWindowOpacity(windowKey);

        var winMenu = new MenuItem
        {
            Header = $"整窗透明度（{Math.Round(currentOpacity * 100)}%）",
        };

        for (var percent = 10; percent <= 100; percent += 10)
        {
            var value = percent / 100d;
            var item = new MenuItem
            {
                Header = $"{percent}%",
                IsCheckable = true,
                IsChecked = Math.Abs(currentOpacity - value) < 0.005,
            };

            item.Click += (_, _) =>
            {
                if (window is null || windowKey is null)
                {
                    skin.SetWindowOpacity(value);
                    return;
                }

                ToolWindowAppearance.Set(window, settings, value);
            };

            winMenu.Items.Add(item);
        }

        if (window is not null && windowKey is not null)
        {
            winMenu.Items.Add(new Separator());

            var follow = new MenuItem
            {
                Header = "跟随全局设置",
                IsCheckable = true,
                IsChecked = !hasOwn,
            };
            follow.Click += (_, _) => ToolWindowAppearance.Reset(window, settings);
            winMenu.Items.Add(follow);
        }

        menu.Items.Add(winMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "提示：改动立即生效", IsEnabled = false });

        return menu;
    }
}
