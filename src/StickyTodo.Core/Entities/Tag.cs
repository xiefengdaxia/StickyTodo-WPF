namespace StickyTodo.Core.Entities;

/// <summary>标签：全局共享，一条待办可挂多个。</summary>
public sealed class Tag
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    /// <summary>用于界面色点，形如 #FFB8860B。</summary>
    public string Color { get; set; } = TagColors.Default;

    public int Sort { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

public static class TagColors
{
    public const string Default = "#FFB8860B";

    /// <summary>新建标签时按顺序取色，保证视觉上可区分。</summary>
    public static readonly string[] Palette =
    {
        "#FFB8860B", // 琥珀
        "#FF2E7D32", // 墨绿
        "#FF1565C0", // 靛蓝
        "#FF6A1B9A", // 紫
        "#FFC62828", // 砖红
        "#FF00838F", // 青
        "#FF4E342E", // 咖
        "#FF37474F", // 石灰蓝
    };

    public static string Pick(int seed) => Palette[Math.Abs(seed) % Palette.Length];
}