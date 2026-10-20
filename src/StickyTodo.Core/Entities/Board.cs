namespace StickyTodo.Core.Entities;

/// <summary>
/// 看板：一个看板对应一个独立窗口。
/// </summary>
public sealed class Board
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public int Sort { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>上次关闭时的窗口位置与大小，没记录过则为 null。</summary>
    public BoardWindowState? Window { get; set; }
}