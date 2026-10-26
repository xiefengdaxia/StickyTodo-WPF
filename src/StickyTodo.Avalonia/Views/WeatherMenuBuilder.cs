using Avalonia.Controls;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.Views;

/// <summary>天气 / 世界时钟窗口共用的外观菜单（皮肤 + 整窗透明度）。</summary>
internal static class WeatherMenuBuilder
{
    public static ContextMenu BuildMenu(SkinService skin, Action onChanged)
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

        var winMenu = new MenuItem { Header = $"整窗透明度（{Math.Round(skin.WindowOpacity * 100)}%）" };
        for (var percent = 10; percent <= 100; percent += 10)
        {
            var value = percent / 100d;
            var item = new MenuItem
            {
                Header = $"{percent}%",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = Math.Abs(skin.WindowOpacity - value) < 0.005,
            };
            item.Click += (_, _) =>
            {
                skin.SetWindowOpacity(value);
                onChanged();
            };
            winMenu.Items.Add(item);
        }

        menu.Items.Add(winMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "提示：改动立即生效", IsEnabled = false });

        return menu;
    }
}
