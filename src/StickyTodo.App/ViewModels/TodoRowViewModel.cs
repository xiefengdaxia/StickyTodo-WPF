using StickyTodo.Core.Entities;
using StickyTodo.Core.Recurrence;
using StickyTodo.Core.Services;

namespace StickyTodo.App.ViewModels;

/// <summary>列表里的一行。轻量包装，避免把领域对象直接暴露给界面绑定。</summary>
public sealed class TodoRowViewModel
{
    public TodoRowViewModel(TodoItem item, string tagText, DateTimeOffset now)
    {
        Item = item;
        TagText = tagText;

        CountdownText = DueDateText.Describe(item.DueAt, now);
        IsOverdue = DueDateText.IsOverdue(item.DueAt, now);
        HasCountdown = CountdownText.Length > 0;

        var remind = item.RemindAt;
        HasReminder = remind is not null;
        RemindText = remind is null
            ? string.Empty
            : remind.Value.Date == now.Date
                ? $"提醒 {remind.Value:HH:mm}"
                : $"提醒 {remind.Value:M月d日 HH:mm}";

        RecurrenceText = RecurrenceEngine.Describe(item.Recurrence);
        IsRecurring = RecurrenceText.Length > 0;
    }

    public TodoItem Item { get; }

    public string Id => Item.Id;

    public string Title => Item.Title;

    public bool IsDone => Item.IsDone;

    public string TagText { get; }

    public bool HasTags => !string.IsNullOrEmpty(TagText);

    /// <summary>「还剩 3 天」「今天到期」「已过期 2 天」。</summary>
    public string CountdownText { get; }

    public bool HasCountdown { get; }

    public bool IsOverdue { get; }

    public string RemindText { get; }

    public bool HasReminder { get; }

    /// <summary>重复规则的中文描述，空串表示不重复。</summary>
    public string RecurrenceText { get; }

    public bool IsRecurring { get; }
}