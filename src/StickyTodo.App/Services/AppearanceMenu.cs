using System.Windows;
using System.Windows.Controls;

namespace StickyTodo.App.Services;

/// <summary>「🎨 外观」菜单：皮肤 + 背景透明度 + 整窗透明度，两个窗口共用。</summary>
internal static class AppearanceMenu
{
    public static void Show(FrameworkElement target, IAppearanceHost host, WindowManager windows)
    {
        var menu = Build(host, windows);
        target.ContextMenu = menu;
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    /// <summary>只构造菜单，不弹出（给回归脚本用）。</summary>
    internal static ContextMenu Build(IAppearanceHost host, WindowManager windows)
    {
        var menu = new ContextMenu();

        var skinMenu = new MenuItem { Header = "皮肤" };
        foreach (var skin in NoteSkins.All)
        {
            var item = new MenuItem
            {
                Header = skin.DisplayName,
                IsCheckable = true,
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

        menu.Items.Add(skinMenu);

        var backgroundMenu = new MenuItem { Header = "背景透明度（文字不变）" };
        for (var percent = 40; percent <= 100; percent += 10)
        {
            var value = percent / 100d;
            var item = new MenuItem
            {
                Header = $"{percent}%",
                IsCheckable = true,
                IsChecked = Math.Abs(host.CurrentOpacity - value) < 0.02,
            };
            item.Click += (_, _) =>
            {
                host.SetOpacity(value);
                windows.ApplySkin();
            };
            backgroundMenu.Items.Add(item);
        }

        menu.Items.Add(backgroundMenu);

        var windowMenu = new MenuItem { Header = "整窗透明度（连文字一起）" };
        for (var percent = 50; percent <= 100; percent += 10)
        {
            var value = percent / 100d;
            var item = new MenuItem
            {
                Header = $"{percent}%",
                IsCheckable = true,
                IsChecked = Math.Abs(host.CurrentWindowOpacity - value) < 0.02,
            };
            item.Click += (_, _) =>
            {
                host.SetWindowOpacity(value);
                windows.ApplySkin();
            };
            windowMenu.Items.Add(item);
        }

        menu.Items.Add(windowMenu);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "背景透明度只让纸变透，文字始终清晰",
            IsEnabled = false,
        });

        return menu;
    }
}