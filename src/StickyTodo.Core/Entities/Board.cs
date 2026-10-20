namespace StickyTodo.Core.Entities;

/// <summary>
/// 看板：一个看板对应一个独立窗口（后续版本），MVP 阶段只有一个默认看板。
/// </summary>
public sealed class Board
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public int Sort { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}