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

        // 弹窗里只能点选，精度不够；这里给"按 1% 微调"和"打开设置页精调"两条路
        var backgroundMenu = new MenuItem
        {
            Header = $"背景透明度：{Math.Round(host.CurrentOpacity * 100)}%（按 1% 微调）",
        };

        AddStep(backgroundMenu, "+1%", 0.01, isBackground: true, host, windows);
        AddStep(backgroundMenu, "−1%", -0.01, isBackground: true, host, windows);
        backgroundMenu.Items.Add(new Separator());
        AddStep(backgroundMenu, "+5%", 0.05, isBackground: true, host, windows);
        AddStep(backgroundMenu, "−5%", -0.05, isBackground: true, host, windows);
        backgroundMenu.Items.Add(new Separator());
        AddPreset(backgroundMenu, "重置为 92%", 0.92, isBackground: true, host, windows);

        menu.Items.Add(backgroundMenu);

        var windowMenu = new MenuItem
        {
            Header = $"整窗透明度：{Math.Round(host.CurrentWindowOpacity * 100)}%（按 1% 微调）",
        };

        AddStep(windowMenu, "+1%", 0.01, isBackground: false, host, windows);
        AddStep(windowMenu, "−1%", -0.01, isBackground: false, host, windows);
        windowMenu.Items.Add(new Separator());
        AddStep(windowMenu, "+5%", 0.05, isBackground: false, host, windows);
        AddStep(windowMenu, "−5%", -0.05, isBackground: false, host, windows);
        windowMenu.Items.Add(new Separator());
        AddPreset(windowMenu, "重置为 100%", 1.0, isBackground: false, host, windows);

        menu.Items.Add(windowMenu);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "精确调整请打开设置页（1% 步进）",
            IsEnabled = false,
        });

        return menu;
    }

    /// <summary>加一个「按步长增减」的菜单项。</summary>
    private static void AddStep(
        MenuItem parent,
        string label,
        double delta,
        bool isBackground,
        IAppearanceHost host,
        WindowManager windows)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) =>
        {
            if (isBackground)
            {
                host.SetOpacity(host.CurrentOpacity + delta);
            }
            else
            {
                host.SetWindowOpacity(host.CurrentWindowOpacity + delta);
            }

            windows.ApplySkin();
        };

        parent.Items.Add(item);
    }

    /// <summary>加一个「重置到固定值」的菜单项。</summary>
    private static void AddPreset(
        MenuItem parent,
        string label,
        double value,
        bool isBackground,
        IAppearanceHost host,
        WindowManager windows)
    {
        var item = new MenuItem { Header = label };
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

        parent.Items.Add(item);
    }
}