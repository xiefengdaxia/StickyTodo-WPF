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

        var skinMenu = new MenuItem { Header = "皮肤" };
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

        // 整窗透明度按窗口类型各存各的（tool.weather 等），不再动全局设置
        var windowKey = ToolWindowOpacity.KeyFor(window);
        var current = settings.GetWindowOpacity(windowKey);
        var winMenu = new MenuItem { Header = $"整窗透明度（{Math.Round(current * 100)}%）" };
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
            Header = "跟随全局设置",
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
        menu.Items.Add(new MenuItem { Header = "提示：改动立即生效", IsEnabled = false });

        return menu;
    }
}
