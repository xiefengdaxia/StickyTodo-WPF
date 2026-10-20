using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Recurrence;
using StickyTodo.Core.Services;
using DoneDateLabel = StickyTodo.Core.Services.DoneDateText;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 列表里的一行。轻量包装，避免把领域对象直接暴露给界面绑定。
/// 标题是「可直接编辑」的：绑定到表格里的 TextBox，失焦/回车时提交。
/// </summary>
public partial class TodoRowViewModel : ObservableObject
{
    public TodoRowViewModel(TodoItem item, string tagText, DateTimeOffset now)
    {
        Item = item;
        TagText = tagText;

        _title = item.Title;

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

        DoneAtText = DoneDateLabel.Describe(item.DoneAt, now);
        HasDoneAt = DoneAtText.Length > 0;
    }

    public TodoItem Item { get; }

    public string Id => Item.Id;

    /// <summary>
    /// 行内可编辑的标题。和 <see cref="TodoItem.Title"/> 分开是因为：
    /// 用户正在输入时不应该立刻落库，也不应该影响行上的其它显示逻辑。
    /// </summary>
    [ObservableProperty]
    private string _title;

    /// <summary>是否处于编辑态（获得焦点时显示更明显的边框）。</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>保存失败时的提示（比如标题为空），显示在行尾。</summary>
    [ObservableProperty]
    private string? _editError;

    /// <summary>拖拽时是不是当前的插入目标（画一条插入线）。</summary>
    [ObservableProperty]
    private bool _isDragTarget;

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

    /// <summary>完成时间，如「今天 14:32」「昨天 09:05」「10月18日 20:11」。</summary>
    public string DoneAtText { get; }

    public bool HasDoneAt { get; }
}