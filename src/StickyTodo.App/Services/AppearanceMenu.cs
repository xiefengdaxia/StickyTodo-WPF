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

        // 弹窗里按 10 档选，和设置页滑块保持一致，比 +1% 好点
        var backgroundMenu = new MenuItem
        {
            Header = $"背景透明度：{Math.Round(host.CurrentOpacity * 100)}%",
        };

        for (var percent = 10; percent <= 100; percent += 10)
        {
            backgroundMenu.Items.Add(BuildOpacityItem(percent, isBackground: true, host, windows));
        }

        menu.Items.Add(backgroundMenu);

        var windowMenu = new MenuItem
        {
            Header = $"整窗透明度：{Math.Round(host.CurrentWindowOpacity * 100)}%",
        };

        for (var percent = 10; percent <= 100; percent += 10)
        {
            windowMenu.Items.Add(BuildOpacityItem(percent, isBackground: false, host, windows));
        }

        menu.Items.Add(windowMenu);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "10% 一档；精确调整请打开设置页",
            IsEnabled = false,
        });

        return menu;
    }

    /// <summary>构造一档透明度菜单项（10% 一档，打勾显示当前值）。</summary>
    private static MenuItem BuildOpacityItem(
        int percent,
        bool isBackground,
        IAppearanceHost host,
        WindowManager windows)
    {
        var value = percent / 100d;
        var current = isBackground ? host.CurrentOpacity : host.CurrentWindowOpacity;

        var item = new MenuItem
        {
            Header = $"{percent}%",
            IsCheckable = true,
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

        return item;
    }
}