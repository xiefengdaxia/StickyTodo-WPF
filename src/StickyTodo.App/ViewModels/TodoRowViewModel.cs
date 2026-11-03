﻿using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Recurrence;
using StickyTodo.Core.Services;
using DoneDateLabel = StickyTodo.Core.Services.DoneDateText;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 列表里的一行。轻量包装，避免把领域对象直接暴露给界面绑定。
/// 标题是「可直接编辑」的：绑定到表格里的 TextBox，失焦/回车时提交。
///
/// 所有派生文本（倒数日 / 提醒 / 重复 / 完成时间）都是可通知属性：
/// 数据变化时调 <see cref="Refresh"/> 原地更新，整个列表不再 Clear+重建，
/// 于是滚动位置、悬停态和正在编辑的行都不会被打断。
/// </summary>
public sealed class TodoRowViewModel : ObservableObject
{
    public TodoRowViewModel(TodoItem item, string tagText, DateTimeOffset now) => Refresh(item, tagText, now);

    private TodoItem _item = null!;

    /// <summary>对应的领域对象（与 ViewModel 里的 <c>_all</c> 是同一个实例）。</summary>
    public TodoItem Item
    {
        get => _item;
        private set
        {
            if (SetProperty(ref _item, value))
            {
                OnPropertyChanged(nameof(Id));
            }
        }
    }

    public string Id => _item.Id;

    private string _title = string.Empty;

    /// <summary>
    /// 行内可编辑的标题。和 <see cref="TodoItem.Title"/> 分开是因为：
    /// 用户正在输入时不应该立刻落库，也不应该影响行上的其它显示逻辑。
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private bool _isEditing;

    /// <summary>是否处于编辑态（获得焦点时显示更明显的边框）。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    private string? _editError;

    /// <summary>保存失败时的提示（比如标题为空），显示在行尾。</summary>
    public string? EditError
    {
        get => _editError;
        set => SetProperty(ref _editError, value);
    }

    private bool _isDragTarget;

    /// <summary>拖拽时是不是当前的插入目标（画一条插入线）。</summary>
    public bool IsDragTarget
    {
        get => _isDragTarget;
        set => SetProperty(ref _isDragTarget, value);
    }

    private bool _isDone;

    public bool IsDone
    {
        get => _isDone;
        private set => SetProperty(ref _isDone, value);
    }

    private string _tagText = string.Empty;

    public string TagText
    {
        get => _tagText;
        private set
        {
            if (SetProperty(ref _tagText, value))
            {
                OnPropertyChanged(nameof(HasTags));
            }
        }
    }

    public bool HasTags => !string.IsNullOrEmpty(_tagText);

    private string _countdownText = string.Empty;

    /// <summary>「还剩 3 天」「今天到期」「已过期 2 天」。</summary>
    public string CountdownText
    {
        get => _countdownText;
        private set => SetProperty(ref _countdownText, value);
    }

    private bool _hasCountdown;

    public bool HasCountdown
    {
        get => _hasCountdown;
        private set => SetProperty(ref _hasCountdown, value);
    }

    private bool _isOverdue;

    public bool IsOverdue
    {
        get => _isOverdue;
        private set => SetProperty(ref _isOverdue, value);
    }

    private string _remindText = string.Empty;

    public string RemindText
    {
        get => _remindText;
        private set => SetProperty(ref _remindText, value);
    }

    private bool _hasReminder;

    public bool HasReminder
    {
        get => _hasReminder;
        private set => SetProperty(ref _hasReminder, value);
    }

    private string _recurrenceText = string.Empty;

    /// <summary>重复规则的中文描述，空串表示不重复。</summary>
    public string RecurrenceText
    {
        get => _recurrenceText;
        private set => SetProperty(ref _recurrenceText, value);
    }

    private bool _isRecurring;

    public bool IsRecurring
    {
        get => _isRecurring;
        private set => SetProperty(ref _isRecurring, value);
    }

    private string _doneAtText = string.Empty;

    /// <summary>完成时间，如「今天 14:32」「昨天 09:05」「10月18日 20:11」。</summary>
    public string DoneAtText
    {
        get => _doneAtText;
        private set => SetProperty(ref _doneAtText, value);
    }

    private bool _hasDoneAt;

    public bool HasDoneAt
    {
        get => _hasDoneAt;
        private set => SetProperty(ref _hasDoneAt, value);
    }

    /// <summary>
    /// 是否有第二行的元信息（完成时间 / 倒数日 / 提醒 / 重复）。
    /// 没有的话第二行整行折叠，行高就能压到最小。
    /// </summary>
    public bool HasMeta => _hasDoneAt || _hasCountdown || _hasReminder || _isRecurring;

    /// <summary>
    /// 用最新的领域对象刷这一行（数据变了的行原地更新，不重建行对象）。
    /// 正在编辑的行不动标题，免得把用户输入中的内容冲掉。
    /// </summary>
    public void Refresh(TodoItem item, string tagText, DateTimeOffset now)
    {
        Item = item;
        if (!IsEditing)
        {
            Title = item.Title;
        }

        TagText = tagText;

        CountdownText = DueDateText.Describe(item.DueAt, now);
        HasCountdown = CountdownText.Length > 0;
        IsOverdue = DueDateText.IsOverdue(item.DueAt, now);

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

        IsDone = item.IsDone;

        OnPropertyChanged(nameof(HasMeta));
    }
}