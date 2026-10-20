using StickyTodo.Core.Calendar;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

public sealed class RecurringTodoTests : DatabaseTestBase
{
    private TodoService CreateService() => new(Repository, new PermanentFreeEntitlement());

    private static DateTimeOffset At(int year, int month, int day, int hour = 9) =>
        new(year, month, day, hour, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void SettingGarbageRecurrence_Throws()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "带规则");

        Assert.Throws<ArgumentException>(() => service.SetRecurrence(todo.Id, "FREQ=NOPE"));
    }

    [Fact]
    public void SetRecurrence_RoundTrips_AndCanBeCleared()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "每天跑步");

        service.SetRecurrence(todo.Id, "FREQ=DAILY");
        Assert.Equal("FREQ=DAILY", service.GetTodos(board.Id)[0].Recurrence);

        service.SetRecurrence(todo.Id, null);
        Assert.Null(service.GetTodos(board.Id)[0].Recurrence);
    }

    [Fact]
    public void GetNextOccurrence_ReturnsTomorrow_ForDaily()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "每日站会", At(2026, 10, 20));
        service.SetRecurrence(todo.Id, "FREQ=DAILY");

        var item = service.GetTodos(board.Id)[0];
        Assert.Equal(At(2026, 10, 21), service.GetNextOccurrence(item));
    }

    [Fact]
    public void CompletingRecurring_RollsDueDate_AndKeepsTodo()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "每天喝水", At(2026, 10, 20));
        service.SetRecurrence(todo.Id, "FREQ=DAILY");

        service.CompleteOccurrence(todo.Id);

        var item = Assert.Single(service.GetTodos(board.Id));
        Assert.False(item.IsDone);                       // 还是待办，不是「完成了」
        Assert.Equal(At(2026, 10, 21), item.DueAt);      // 目标日期滚到下一次
        Assert.Single(service.GetCompletions(todo.Id));  // 记了一笔完成历史
    }

    [Fact]
    public void CompletingNonRecurring_MarksDone()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "一次性任务", At(2026, 10, 20));

        service.CompleteOccurrence(todo.Id);

        Assert.True(Assert.Single(service.GetTodos(board.Id)).IsDone);
        Assert.Empty(service.GetCompletions(todo.Id));
    }

    [Fact]
    public void CompletingRecurringWithCountLimit_EventuallyCompletes()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "只做两次", At(2026, 10, 20));
        service.SetRecurrence(todo.Id, "FREQ=DAILY;COUNT=1"); // 只有起始这一次

        service.CompleteOccurrence(todo.Id);

        var item = Assert.Single(service.GetTodos(board.Id));
        Assert.True(item.IsDone);
    }

    [Fact]
    public void CompletingRecurring_MovesReminderToo()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "每天的提醒", At(2026, 10, 20));
        service.SetRecurrence(todo.Id, "FREQ=DAILY");
        service.SetRemindAt(todo.Id, At(2026, 10, 20));

        service.CompleteOccurrence(todo.Id);

        var item = Assert.Single(service.GetTodos(board.Id));
        Assert.Equal(At(2026, 10, 21), item.RemindAt);
    }

    [Fact]
    public void Completions_AreNewestFirst()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "记录历史", At(2026, 10, 20));
        service.SetRecurrence(todo.Id, "FREQ=DAILY");

        service.CompleteOccurrence(todo.Id);
        service.CompleteOccurrence(todo.Id);

        var completions = service.GetCompletions(todo.Id);
        Assert.Equal(2, completions.Count);
        Assert.True(completions[0].CompletedAt >= completions[1].CompletedAt);
    }

    [Fact]
    public void Calendar_ShowsRecurringTodoOnEachOccurrence()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "每周例会", At(2026, 10, 20)); // 周二
        service.SetRecurrence(todo.Id, "FREQ=WEEKLY");

        var entries = service.GetCalendarEntries(At(2026, 10, 1), At(2026, 10, 31, 23));

        // 10-20(周二)、10-27(周二) 各一次；下一次 11-03 已在区间外
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(DayOfWeek.Tuesday, e.Occurrence.DayOfWeek));
    }

    [Fact]
    public void Calendar_IgnoresCompletedTodos()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "做完了", At(2026, 10, 20));
        service.SetDone(todo.Id, true);

        Assert.Empty(service.GetCalendarEntries(At(2026, 10, 1), At(2026, 10, 31, 23)));
    }

    [Fact]
    public void Calendar_IncludesNonRecurringTodoOnce()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        service.AddTodo(board.Id, "单次", At(2026, 10, 20));

        var entries = service.GetCalendarEntries(At(2026, 10, 1), At(2026, 10, 31, 23));
        Assert.Single(entries);
        Assert.False(entries[0].IsVirtual);
    }
}

public sealed class CalendarProjectionTests
{
    private static TodoItem Todo(string id, DateTimeOffset? due, string? rrule = null) => new()
    {
        Id = id,
        BoardId = "b",
        Title = id,
        DueAt = due,
        Recurrence = rrule,
        CreatedAt = due ?? DateTimeOffset.Now,
        UpdatedAt = due ?? DateTimeOffset.Now,
    };

    private static readonly DateTimeOffset Oct1 = new(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Oct31 = new(2026, 10, 31, 23, 59, 0, TimeSpan.FromHours(8));

    [Fact]
    public void NonRecurring_OutsideRange_IsSkipped()
    {
        var entries = CalendarProjection.Project(
            [Todo("x", new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(8)))], Oct1, Oct31);

        Assert.Empty(entries);
    }

    [Fact]
    public void Recurring_ExpandsAndMarksVirtual()
    {
        var master = Todo("daily", new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(8)), "FREQ=DAILY");
        var entries = CalendarProjection.Project([master], Oct1, Oct31);

        Assert.Equal(27, entries.Count);                     // 10-05 ~ 10-31
        Assert.Single(entries.Where(e => !e.IsVirtual));     // 只有第一次是真身
        Assert.Equal(new DateOnly(2026, 10, 5), entries[0].Day);
    }

    [Fact]
    public void Recurring_WithoutDue_UsesCreatedAtAsAnchor()
    {
        var master = Todo("no-due", null, "FREQ=DAILY");
        master = new TodoItem
        {
            Id = master.Id,
            BoardId = master.BoardId,
            Title = master.Title,
            Recurrence = master.Recurrence,
            CreatedAt = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(8)),
            UpdatedAt = DateTimeOffset.Now,
        };

        var entries = CalendarProjection.Project([master], Oct1, Oct31);
        Assert.NotEmpty(entries);
        Assert.Equal(new DateOnly(2026, 10, 8), entries[0].Day);
    }

    [Fact]
    public void Entries_AreSortedByTime()
    {
        var a = Todo("late", new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.FromHours(8)));
        var b = Todo("early", new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(8)));

        var entries = CalendarProjection.Project([a, b], Oct1, Oct31);
        Assert.Equal("early", entries[0].Todo.Id);
    }
}