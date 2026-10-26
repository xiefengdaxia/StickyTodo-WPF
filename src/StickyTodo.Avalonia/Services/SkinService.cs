using Avalonia;
using Avalonia.Styling;
using StickyTodo.Core.Services;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 把当前皮肤写进 Application.Resources。
/// XAML 里用 DynamicResource 引用这些键，换皮肤时所有窗口立即生效。
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

    public double WindowOpacity => _settings.WindowOpacity;

    /// <summary>按当前设置（或指定皮肤/透明度）刷新资源。</summary>
    public void Apply(string? skinKey = null, double? opacity = null)
    {
        var skin = NoteSkins.Resolve(skinKey ?? _settings.Skin);
        var alpha = AppSettingsService.ClampOpacity(opacity ?? _settings.Opacity);
        Current = skin;

        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        resources["Brush.Paper"] = skin.CreateBackground(alpha);
        resources["Brush.PaperDeep"] = skin.CreateHeaderOverlay(alpha);
        // 主窗体与设置页要"有圆角但完全不透明"，单独一份不透明纸色
        resources["Brush.PaperOpaque"] = skin.CreateBackground(1.0);
        resources["Brush.Ink"] = new Avalonia.Media.SolidColorBrush(skin.Ink);
        resources["Brush.InkSoft"] = new Avalonia.Media.SolidColorBrush(skin.InkSoft);
        resources["Brush.Accent"] = new Avalonia.Media.SolidColorBrush(skin.Accent);
        resources["Brush.Line"] = skin.CreateLineBrush();
        resources["Brush.ChipIdle"] = skin.CreateChipIdleBrush();
        resources["Brush.ChipActive"] = new Avalonia.Media.SolidColorBrush(skin.Accent);
        resources["Brush.Surface"] = skin.CreateSurfaceBrush();
        resources["Brush.SurfaceHover"] = skin.CreateSurfaceHoverBrush();
        resources["Brush.AccentSoft"] = skin.CreateAccentSoftBrush();
        resources["Brush.Track"] = skin.CreateTrackBrush();
        resources["Bool.IsDark"] = skin.IsDark;
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

    /// <summary>调整整窗透明度并持久化（连文字一起变淡）。</summary>
    public void SetWindowOpacity(double opacity)
    {
        _settings.WindowOpacity = opacity;
    }
}
