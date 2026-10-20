namespace StickyTodo.Core.Entities;

/// <summary>
/// 便签/随笔看板的正文。一个看板一条记录（将来要支持一个看板多张便签，加列即可）。
/// </summary>
public sealed class Note
{
    public required string Id { get; init; }

    public required string BoardId { get; init; }

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}