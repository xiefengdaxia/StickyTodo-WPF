using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Calendar;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;

namespace StickyTodo.Core.Services;

/// <summary>
/// 业务用例层：所有写操作先过权益闸门，再落仓库。
/// 界面只调这里，不直接碰仓储，也不自己判断会员/额度。
/// </summary>
public sealed class TodoService
{
    public const string DefaultBoardName = "我的待办";

    private readonly ITodoRepository _repository;
    private readonly IEntitlementService _entitlement;

    public TodoService(ITodoRepository repository, IEntitlementService entitlement)
    {
        _repository = repository;
        _entitlement = entitlement;
    }

    public IEntitlementService Entitlement => _entitlement;

    // ---------- 看板 ----------

    /// <summary>首次启动时保证至少有一个默认看板。</summary>
    public Board EnsureDefaultBoard()
    {
        var boards = _repository.GetBoards();
        if (boards.Count > 0)
        {
            return boards.FirstOrDefault(b => b.IsDefault) ?? boards[0];
        }

        return _repository.AddBoard(DefaultBoardName, isDefault: true);
    }

    public IReadOnlyList<Board> GetBoards() => _repository.GetBoards();

    public Board? GetBoard(string boardId) => _repository.GetBoards().FirstOrDefault(b => b.Id == boardId);

    public Board CreateBoard(string name, BoardKind kind = BoardKind.Todo)
    {
        var clean = Require(name, "看板名称");
        if (_repository.GetBoards().Count >= _entitlement.MaxBoards)
        {
            throw new EntitlementLimitException(
                $"{_entitlement.DisplayName}最多支持 {_entitlement.MaxBoards} 个看板，当前已达上限。");
        }

        return _repository.AddBoard(clean, kind);
    }

    public bool RenameBoard(string boardId, string name) =>
        _repository.RenameBoard(boardId, Require(name, "看板名称"));

    public bool DeleteBoard(string boardId)
    {
        var boards = _repository.GetBoards();
        if (boards.Count <= 1)
        {
            throw new InvalidOperationException("至少要保留一个看板。");
        }

        return _repository.DeleteBoard(boardId);
    }

    public void SaveBoardWindow(string boardId, BoardWindowState state)
    {
        if (state.IsValid)
        {
            _repository.SaveBoardWindow(boardId, state);
        }
    }

    // ---------- 待办 ----------

    public IReadOnlyList<TodoItem> GetTodos(string boardId, bool includeDone = true) =>
        _repository.GetTodos(boardId, includeDone);

    public TodoItem AddTodo(string boardId, string title, DateTimeOffset? dueAt = null) =>
        _repository.AddTodo(boardId, Require(title, "待办内容"), dueAt);

    public void SetDone(string id, bool done) => _repository.SetTodoDone(id, done);

    public void Delete(string id) => _repository.DeleteTodo(id);

    /// <summary>改标题（行内编辑用）。清空由调用方决定是删除还是忽略。</summary>
    public void UpdateTitle(string id, string title)
    {
        var clean = Require(title, "待办内容");
        var item = _repository.GetTodo(id) ?? throw new InvalidOperationException($"待办不存在：{id}");
        item.Title = clean;
        item.UpdatedAt = DateTimeOffset.Now;
        _repository.UpdateTodo(item);
    }

    public void UpdateNote(string id, string? note)
    {
        var item = _repository.GetTodo(id) ?? throw new InvalidOperationException($"待办不存在：{id}");
        item.Note = note;
        item.UpdatedAt = DateTimeOffset.Now;
        _repository.UpdateTodo(item);
    }

    /// <summary>把待办拖到另一条之前（targetId 为 null 表示放到末尾）。</summary>
    public void MoveBefore(string boardId, string movingId, string? targetId)
    {
        var ordered = _repository.GetTodos(boardId)
            .OrderBy(t => t.Sort)
            .Select(t => t.Id)
            .ToList();

        _repository.ReorderTodos(boardId, TodoOrdering.MoveBefore(ordered, movingId, targetId));
    }

    // ---------- 标签 ----------

    public IReadOnlyList<Tag> GetTags() => _repository.GetTags();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetTodoTagMap(string boardId) =>
        _repository.GetTodoTagMap(boardId);

    public Tag CreateTag(string name)
    {
        var clean = Require(name, "标签名称");
        if (_repository.GetTags().Any(t => string.Equals(t.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"标签「{clean}」已经存在。");
        }

        var tags = _repository.GetTags();
        if (tags.Count >= _entitlement.MaxTags)
        {
            throw new EntitlementLimitException(
                $"{_entitlement.DisplayName}最多支持 {_entitlement.MaxTags} 个标签，当前已达上限。");
        }

        return _repository.AddTag(clean, TagColors.Pick(tags.Count));
    }

    public bool RenameTag(string tagId, string name)
    {
        var clean = Require(name, "标签名称");
        if (_repository.GetTags().Any(t =>
                t.Id != tagId && string.Equals(t.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"标签「{clean}」已经存在。");
        }

        return _repository.RenameTag(tagId, clean);
    }

    public bool DeleteTag(string tagId) => _repository.DeleteTag(tagId);

    public void SetTodoTags(string todoId, IReadOnlyList<string> tagIds) =>
        _repository.SetTodoTags(todoId, tagIds);

    // ---------- 便签/随笔 ----------

    public Note? GetNote(string boardId) => _repository.GetNote(boardId);

    public Note SaveNote(string boardId, string content) =>
        _repository.SaveNote(boardId, content ?? string.Empty);

    // ---------- 倒数日与提醒 ----------

    public IReadOnlyList<TodoItem> GetDueReminders(DateTimeOffset now) =>
        _repository.GetDueReminders(now);

    public void SetDueAt(string todoId, DateTimeOffset? dueAt)
    {
        var item = _repository.GetTodo(todoId) ?? throw new InvalidOperationException($"待办不存在：{todoId}");
        item.DueAt = dueAt;
        item.UpdatedAt = DateTimeOffset.Now;
        _repository.SetDueAt(todoId, dueAt);
    }

    public void SetRemindAt(string todoId, DateTimeOffset? remindAt)
    {
        _repository.SetRemindAt(todoId, remindAt);
        _repository.MarkReminded(todoId, null);
    }

    public void MarkReminded(string todoId, DateTimeOffset remindedAt) =>
        _repository.MarkReminded(todoId, remindedAt);

    /// <summary>稍后提醒：把提醒时间往后推。</summary>
    public void Snooze(string todoId, TimeSpan delay)
    {
        _repository.SetRemindAt(todoId, DateTimeOffset.Now.Add(delay));
        _repository.MarkReminded(todoId, null);
    }

    // ---------- 重复待办 ----------

    public void SetRecurrence(string todoId, string? rrule)
    {
        if (!string.IsNullOrWhiteSpace(rrule) && !Recurrence.RecurrenceEngine.IsValid(rrule))
        {
            throw new ArgumentException($"看不懂的重复规则：{rrule}", nameof(rrule));
        }

        _repository.SetRecurrence(todoId, string.IsNullOrWhiteSpace(rrule) ? null : rrule.Trim());
    }

    /// <summary>这条重复待办下一次什么时候发生（没有规则或已经结束返回 null）。</summary>
    public DateTimeOffset? GetNextOccurrence(TodoItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Recurrence))
        {
            return null;
        }

        var anchor = item.DueAt ?? item.CreatedAt;
        return Recurrence.RecurrenceEngine.Next(anchor, item.Recurrence, anchor);
    }

    /// <summary>
    /// 勾选完成。重复待办不是「做完就没了」：记一笔完成历史，把目标日期滚到下一次，
    /// 提醒也重置，下次到点会再响。
    /// </summary>
    public void CompleteOccurrence(string todoId)
    {
        var item = _repository.GetTodo(todoId) ?? throw new InvalidOperationException($"待办不存在：{todoId}");

        var next = GetNextOccurrence(item);
        if (next is null)
        {
            SetDone(todoId, true);
            return;
        }

        var occurrence = item.DueAt ?? DateTimeOffset.Now;
        _repository.AddCompletion(todoId, occurrence, DateTimeOffset.Now);
        _repository.SetDueAt(todoId, next);
        if (item.RemindAt is not null)
        {
            // 提醒时间跟着往后挪一格
            _repository.SetRemindAt(todoId, next);
            _repository.MarkReminded(todoId, null);
        }
    }

    public IReadOnlyList<TodoCompletion> GetCompletions(string todoId, int limit = 50) =>
        _repository.GetCompletions(todoId, limit);

    // ---------- 日历 ----------

    public IReadOnlyList<CalendarEntry> GetCalendarEntries(DateTimeOffset from, DateTimeOffset to, bool includeDone = false)
    {
        var todos = _repository.GetTodosWithDue(from, to, includeDone);
        return CalendarProjection.Project(todos, from, to);
    }

    private static string Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{field}不能为空。");
        }

        return value.Trim();
    }
}