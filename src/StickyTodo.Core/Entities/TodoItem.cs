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

    /// <summary>到点提醒的时刻，null 表示不提醒。</summary>
    public DateTimeOffset? RemindAt { get; set; }

    /// <summary>已经弹过提醒的时间，避免重复打扰。</summary>
    public DateTimeOffset? RemindedAt { get; set; }

    /// <summary>重复规则（RFC 5545 RRULE，如 FREQ=WEEKLY;BYDAY=MO,WE），null 表示不重复。</summary>
    public string? Recurrence { get; set; }

    public int Sort { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}