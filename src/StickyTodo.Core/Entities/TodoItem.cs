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

    /// <summary>
    /// 四象限归属：0=①重要且紧急，1=②重要不紧急，2=③紧急不重要，3=④都不重要；
    /// null = 还没归类（界面上默认按 ② 显示，拖动后才写值）。
    /// </summary>
    public int? Quadrant { get; set; }

    public int Sort { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}