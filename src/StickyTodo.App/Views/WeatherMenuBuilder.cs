using System.Windows;
using System.Windows.Controls;
using StickyTodo.App.Services;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Views;

/// <summary>
/// 天气 / 世界时钟 / 网速 / 性能窗口共用的外观菜单：皮肤 + 背景透明度 + 整窗透明度。
/// 这些窗口不实现 IAppearanceHost（它们不参与看板间同步），
/// 背景透明度是全局资源，整窗透明度按窗口类型单独存。
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
        var skinMenu = new MenuItem { Header = Loc.T("menu.skin") };
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

        // 背景透明度：调的是纸色 alpha（全局共用），和看板 🎨 菜单里的那一项一致。
        // 之前这里只有整窗透明度，天气/世界时钟/网速/性能就没法单独把纸调透。
        var bgMenu = new MenuItem
        {
            Header = Loc.F("menu.bgOpacity", Math.Round(skin.Opacity * 100)),
        };

        for (var percent = 10; percent <= 100; percent += 10)
        {
            var value = percent / 100d;
            var item = new MenuItem
            {
                Header = $"{percent}%",
                IsCheckable = true,
                IsChecked = Math.Abs(skin.Opacity - value) < 0.005,
            };

            item.Click += (_, _) => skin.SetOpacity(value);
            bgMenu.Items.Add(item);
        }

        menu.Items.Add(bgMenu);

        // 整窗透明度：默认跟随全局，也可以给「这类窗口」单独设一个值
        var currentOpacity = windowKey is null ? settings.WindowOpacity : settings.GetWindowOpacity(windowKey);
        var hasOwn = windowKey is not null && settings.HasWindowOpacity(windowKey);

        var winMenu = new MenuItem
        {
            Header = Loc.F("menu.winOpacity", Math.Round(currentOpacity * 100)),
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
                Header = Loc.T("menu.opacityFollowGlobal"),
                IsCheckable = true,
                IsChecked = !hasOwn,
            };
            follow.Click += (_, _) => ToolWindowAppearance.Reset(window, settings);
            winMenu.Items.Add(follow);
        }

        menu.Items.Add(winMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Loc.T("menu.opacityHint"), IsEnabled = false });

        return menu;
    }
}
