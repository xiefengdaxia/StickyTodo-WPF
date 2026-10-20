namespace StickyTodo.Core.Entities;

/// <summary>
/// 重复待办每次完成的记录。重复待办不会「做完就消失」，
/// 而是记一笔完成历史，再把目标日期滚到下一次。
/// </summary>
public sealed class TodoCompletion
{
    public required string Id { get; init; }

    public required string TodoId { get; init; }

    /// <summary>这一次对应的是哪一天（滚动前的那次目标日期）。</summary>
    public DateTimeOffset OccurrenceAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}