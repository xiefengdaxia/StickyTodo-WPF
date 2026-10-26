using Avalonia.Media;
using StickyTodo.Core.Services;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 一套便签配色。颜色只属于界面层，所以放在 App 工程而不是 Core。
/// Avalonia 版用 <see cref="Color"/> + <see cref="Brush"/>，语义与 WPF 版一致。
/// </summary>
public sealed record NoteSkin(
    string Key,
    string DisplayName,
    Color BackgroundTop,
    Color BackgroundBottom,
    Color Ink,
    Color InkSoft,
    Color Accent,
    bool IsDark)
{
    /// <summary>
    /// 背景渐变。<paramref name="opacity"/> 只作用在背景上，文字始终不透明。
    /// 100% 时返回不带 alpha 的纯渐变——否则叠上窗口自身的半透明后看起来还会"透"。
    /// </summary>
    public IBrush CreateBackground(double opacity)
    {
        var clamped = Math.Clamp(opacity, 0.05, 1.0);

        if (clamped >= 0.999)
        {
            return new LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
                EndPoint = new Avalonia.RelativePoint(0.15, 1, Avalonia.RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(BackgroundTop, 0),
                    new GradientStop(BackgroundBottom, 1),
                },
            };
        }

        var alpha = (byte)Math.Round(clamped * 255);
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(0.15, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithAlpha(BackgroundTop, alpha), 0),
                new GradientStop(WithAlpha(BackgroundBottom, alpha), 1),
            },
        };
    }

    /// <summary>标题栏压暗/提亮一层，让标题区和内容区有分界。</summary>
    public IBrush CreateHeaderOverlay(double opacity) =>
        Solid(WithAlpha(Ink, (byte)Math.Round(Math.Clamp(opacity, 0.05, 1.0) * (IsDark ? 0.16 : 0.10) * 255)));

    public IBrush CreateLineBrush() => Solid(WithAlpha(Ink, 0x24));

    /// <summary>卡片底色：比纸面亮一层，形成层次而不抢眼。</summary>
    public IBrush CreateSurfaceBrush() =>
        Solid(IsDark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));

    /// <summary>卡片悬停底色。</summary>
    public IBrush CreateSurfaceHoverBrush() =>
        Solid(IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));

    /// <summary>主色的淡底，用于图标方块、徽标。</summary>
    public IBrush CreateAccentSoftBrush() => Solid(WithAlpha(Accent, IsDark ? (byte)0x3D : (byte)0x26));

    public IBrush CreateChipIdleBrush() =>
        Solid(IsDark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    /// <summary>滑块的底轨颜色。</summary>
    public IBrush CreateTrackBrush() => Solid(WithAlpha(Ink, 0x1F));

    /// <summary>色卡预览用：不受全局透明度影响。</summary>
    public IBrush CreateSwatchBrush() => new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(0.4, 1, Avalonia.RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(BackgroundTop, 0),
            new GradientStop(BackgroundBottom, 1),
        },
    };

    private static IBrush Solid(Color color) => new SolidColorBrush(color);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}

/// <summary>内置皮肤。加一套配色只需在这里加一条。</summary>
public static class NoteSkins
{
    public static readonly NoteSkin ClassicYellow = new(
        "sticky-yellow", "经典黄",
        Color.FromRgb(0xFF, 0xF4, 0xB8), Color.FromRgb(0xFB, 0xE1, 0x8A),
        Color.FromRgb(0x3B, 0x38, 0x2A), Color.FromRgb(0x7C, 0x74, 0x5E),
        Color.FromRgb(0xB2, 0x7F, 0x0A), false);

    public static readonly NoteSkin Mint = new(
        "mint", "薄荷绿",
        Color.FromRgb(0xD8, 0xF5, 0xE0), Color.FromRgb(0xB4, 0xEA, 0xC6),
        Color.FromRgb(0x1E, 0x3A, 0x2C), Color.FromRgb(0x5C, 0x7A, 0x69),
        Color.FromRgb(0x2E, 0x7D, 0x5B), false);

    public static readonly NoteSkin Sky = new(
        "sky", "天青蓝",
        Color.FromRgb(0xDB, 0xEA, 0xFB), Color.FromRgb(0xB9, 0xD8, 0xF4),
        Color.FromRgb(0x1E, 0x2F, 0x3F), Color.FromRgb(0x5A, 0x70, 0x85),
        Color.FromRgb(0x1B, 0x5E, 0xB8), false);

    public static readonly NoteSkin Violet = new(
        "violet", "淡紫",
        Color.FromRgb(0xEE, 0xE1, 0xFB), Color.FromRgb(0xD8, 0xC2, 0xF3),
        Color.FromRgb(0x32, 0x22, 0x49), Color.FromRgb(0x6C, 0x5A, 0x84),
        Color.FromRgb(0x6A, 0x1B, 0x9A), false);

    public static readonly NoteSkin Rose = new(
        "rose", "樱花粉",
        Color.FromRgb(0xFF, 0xE2, 0xEB), Color.FromRgb(0xFA, 0xC8, 0xD7),
        Color.FromRgb(0x42, 0x22, 0x2F), Color.FromRgb(0x88, 0x61, 0x70),
        Color.FromRgb(0xC2, 0x18, 0x5B), false);

    public static readonly NoteSkin Paper = new(
        "paper", "素白",
        Color.FromRgb(0xFA, 0xFA, 0xF7), Color.FromRgb(0xEB, 0xEA, 0xE4),
        Color.FromRgb(0x24, 0x24, 0x22), Color.FromRgb(0x6E, 0x6E, 0x68),
        Color.FromRgb(0x2F, 0x6F, 0x4F), false);

    public static readonly NoteSkin Graphite = new(
        "graphite", "石墨夜色",
        Color.FromRgb(0x3C, 0x41, 0x47), Color.FromRgb(0x22, 0x25, 0x29),
        Color.FromRgb(0xF0, 0xF1, 0xF3), Color.FromRgb(0x9E, 0xA5, 0xAD),
        Color.FromRgb(0x7E, 0xC8, 0xE3), true);

    public static readonly NoteSkin Midnight = new(
        "midnight", "午夜墨蓝",
        Color.FromRgb(0x1F, 0x2A, 0x3C), Color.FromRgb(0x14, 0x1C, 0x2A),
        Color.FromRgb(0xE8, 0xEE, 0xF7), Color.FromRgb(0x8E, 0x9C, 0xB4),
        Color.FromRgb(0x8A, 0xB4, 0xF8), true);

    /// <summary>暖橙亮色：介于黄与粉之间，偏"日落"。</summary>
    public static readonly NoteSkin Apricot = new(
        "apricot", "暖橘杏",
        Color.FromRgb(0xFF, 0xEC, 0xD6), Color.FromRgb(0xF9, 0xCF, 0xA6),
        Color.FromRgb(0x43, 0x2C, 0x1B), Color.FromRgb(0x8A, 0x69, 0x4E),
        Color.FromRgb(0xC2, 0x5E, 0x0E), false);

    /// <summary>暖调暗色：深棕咖，和冷调的两套暗色形成对比。</summary>
    public static readonly NoteSkin Espresso = new(
        "espresso", "深棕咖",
        Color.FromRgb(0x3A, 0x2E, 0x27), Color.FromRgb(0x22, 0x1A, 0x15),
        Color.FromRgb(0xF2, 0xEA, 0xE2), Color.FromRgb(0xA8, 0x97, 0x89),
        Color.FromRgb(0xE8, 0xA8, 0x5C), true);

    public static readonly IReadOnlyList<NoteSkin> All =
    [
        ClassicYellow, Paper, Mint, Sky, Violet, Rose, Apricot,
        Graphite, Midnight, Espresso,
    ];

    public static NoteSkin Resolve(string? key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) ?? ClassicYellow;
}
