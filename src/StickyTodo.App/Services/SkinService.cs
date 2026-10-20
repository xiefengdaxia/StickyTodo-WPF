using System.Windows;
using System.Windows.Media;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Services;

/// <summary>
/// 把当前皮肤写进 Application.Resources。
/// XAML 里用 DynamicResource 引用这些键，所以换皮肤时所有窗口立即生效。
/// </summary>
public sealed class SkinService
{
    private readonly AppSettingsService _settings;

    public SkinService(AppSettingsService settings)
    {
        _settings = settings;
    }

    public NoteSkin Current { get; private set; } = NoteSkins.ClassicYellow;

    public double Opacity => _settings.Opacity;

    /// <summary>按当前设置（或指定皮肤/透明度）刷新资源。</summary>
    public void Apply(string? skinKey = null, double? opacity = null)
    {
        var skin = NoteSkins.Resolve(skinKey ?? _settings.Skin);
        var alpha = AppSettingsService.ClampOpacity(opacity ?? _settings.Opacity);
        Current = skin;

        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        resources["Brush.Paper"] = skin.CreateBackground(alpha);
        resources["Brush.PaperDeep"] = skin.CreateHeaderOverlay(alpha);
        resources["Brush.Ink"] = new SolidColorBrush(skin.Ink);
        resources["Brush.InkSoft"] = new SolidColorBrush(skin.InkSoft);
        resources["Brush.Accent"] = new SolidColorBrush(skin.Accent);
        resources["Brush.Line"] = skin.CreateLineBrush();
        resources["Brush.ChipIdle"] = skin.CreateChipIdleBrush();
        resources["Brush.ChipActive"] = new SolidColorBrush(skin.Accent);
        resources["Brush.WindowBackdrop"] = Brushes.Transparent;
    }

    /// <summary>切换皮肤并持久化。</summary>
    public void SetSkin(string skinKey)
    {
        var skin = NoteSkins.Resolve(skinKey);
        _settings.Skin = skin.Key;
        Apply(skin.Key);
    }

    /// <summary>调整背景透明度并持久化。</summary>
    public void SetOpacity(double opacity)
    {
        _settings.Opacity = opacity;
        Apply(opacity: _settings.Opacity);
    }
}