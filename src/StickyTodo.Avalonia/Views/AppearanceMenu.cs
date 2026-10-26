using Avalonia.Controls;
using StickyTodo.Core.Localization;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.Views;

/// <summary>「🎨 外观」菜单：皮肤 + 背景透明度 + 整窗透明度，两个窗口共用。</summary>
internal static class AppearanceMenu
{
    public static ContextMenu Build(IAppearanceHost host, WindowManager windows)
    {
        var menu = new ContextMenu();
        menu.Items.Add(BuildSkinMenu(host, windows));
        menu.Items.Add(BuildOpacityMenu(Loc.F("menu.bgOpacity", Math.Round(host.CurrentOpacity * 100)), isBackground: true, host, windows));
        menu.Items.Add(BuildOpacityMenu(Loc.F("menu.winOpacity", Math.Round(host.CurrentWindowOpacity * 100)), isBackground: false, host, windows));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Loc.T("menu.opacityHint"), IsEnabled = false });
        return menu;
    }

    private static MenuItem BuildSkinMenu(IAppearanceHost host, WindowManager windows)
    {
        var skinMenu = new MenuItem { Header = Loc.T("menu.skin") };

        foreach (var skin in NoteSkins.All)
        {
            var item = new MenuItem
            {
                Header = skin.DisplayName,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = string.Equals(skin.Key, host.CurrentSkinKey, StringComparison.OrdinalIgnoreCase),
            };

            var key = skin.Key;
            item.Click += (_, _) =>
            {
                host.SetSkin(key);
                windows.ApplySkin();
            };

            skinMenu.Items.Add(item);
        }

        return skinMenu;
    }

    /// <summary>构造一档透明度菜单项（10% 一档，打勾显示当前值）。</summary>
    private static MenuItem BuildOpacityMenu(
        string header,
        bool isBackground,
        IAppearanceHost host,
        WindowManager windows)
    {
        var root = new MenuItem { Header = header };
        var current = isBackground ? host.CurrentOpacity : host.CurrentWindowOpacity;

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
                if (isBackground)
                {
                    host.SetOpacity(value);
                }
                else
                {
                    host.SetWindowOpacity(value);
                }

                windows.ApplySkin();
            };

            root.Items.Add(item);
        }

        return root;
    }
}
