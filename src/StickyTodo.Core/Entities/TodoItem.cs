namespace StickyTodo.Core.Entities;

/// <summary>
/// 待办条目。时间统一用 <see cref="DateTimeOffset"/>，落库转 Unix 秒，避免时区歧义。
/// </summary>
public sealed class TodoItem
{
    public required string Id { get; init; }

    public required string BoardId { get; init; }

    public required string Title { get; set; }

    public string? Note { get; set; }

    public bool IsDone { get; set; }

    public DateTimeOffset? DoneAt { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public int Sort { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}