using Avalonia.Controls;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.Views;

/// <summary>工具窗（天气 / 世界时钟 / 网速 / 性能）共用的外观菜单（皮肤 + 整窗透明度）。</summary>
internal static class WeatherMenuBuilder
{
    public static ContextMenu BuildMenu(AppSettingsService settings, SkinService skin, Window window, Action onChanged)
    {
        var menu = new ContextMenu();

        var skinMenu = new MenuItem { Header = StickyTodo.Core.Localization.Loc.T("menu.skin") };
        foreach (var s in NoteSkins.All)
        {
            var item = new MenuItem
            {
                Header = s.DisplayName,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = string.Equals(s.Key, skin.Current.Key, StringComparison.OrdinalIgnoreCase),
            };
            var key = s.Key;
            item.Click += (_, _) =>
            {
                skin.SetSkin(key);
                onChanged();
            };
            skinMenu.Items.Add(item);
        }

        menu.Items.Add(skinMenu);

        // 背景透明度：调纸色 alpha（全局共用），和看板 🎨 菜单里的那一项一致
        var bgMenu = new MenuItem { Header = StickyTodo.Core.Localization.Loc.F("menu.bgOpacity", Math.Round(skin.Opacity * 100)) };
        for (var percent = 10; percent <= 100; percent += 10)
        {
            var value = percent / 100d;
            var item = new MenuItem
            {
                Header = $"{percent}%",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = Math.Abs(skin.Opacity - value) < 0.005,
            };
            item.Click += (_, _) =>
            {
                skin.SetOpacity(value);
                onChanged();
            };
            bgMenu.Items.Add(item);
        }

        menu.Items.Add(bgMenu);

        // 整窗透明度按窗口类型各存各的（tool.weather 等），不再动全局设置
        var windowKey = ToolWindowOpacity.KeyFor(window);
        var current = settings.GetWindowOpacity(windowKey);
        var winMenu = new MenuItem { Header = StickyTodo.Core.Localization.Loc.F("menu.winOpacity", Math.Round(current * 100)) };
        for (var percent = 10; percent <= 100; percent += 10)
        {
            var value = percent / 100d;
            var item = new MenuItem
            {
                Header = $"{percent}%",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = Math.Abs(current - value) < 0.005,
            };
            item.Click += (_, _) =>
            {
                ToolWindowOpacity.Set(window, settings, value);
                onChanged();
            };
            winMenu.Items.Add(item);
        }

        winMenu.Items.Add(new Separator());

        var followGlobal = new MenuItem
        {
            Header = StickyTodo.Core.Localization.Loc.T("menu.opacityFollowGlobal"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = !settings.HasWindowOpacity(windowKey),
        };
        followGlobal.Click += (_, _) =>
        {
            ToolWindowOpacity.Reset(window, settings);
            onChanged();
        };
        winMenu.Items.Add(followGlobal);

        menu.Items.Add(winMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = StickyTodo.Core.Localization.Loc.T("menu.opacityHint"), IsEnabled = false });

        return menu;
    }
}
