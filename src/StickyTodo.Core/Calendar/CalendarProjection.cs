using StickyTodo.Core.Entities;
using StickyTodo.Core.Recurrence;

namespace StickyTodo.Core.Calendar;

/// <summary>日历上的一条占位：普通待办是它自己的目标日期，重复待办会被展开成多次。</summary>
public sealed record CalendarEntry(TodoItem Todo, DateTimeOffset Occurrence, bool IsVirtual)
{
    public bool IsRecurring => !string.IsNullOrWhiteSpace(Todo.Recurrence);

    public DateOnly Day => DateOnly.FromDateTime(Occurrence.LocalDateTime);
}

/// <summary>
/// 把待办投影到某个日期区间：
/// 非重复的按目标日期落一次；重复的按 RRULE 展开成多次（不落库，纯计算）。
/// </summary>
public static class CalendarProjection
{
    public static IReadOnlyList<CalendarEntry> Project(
        IEnumerable<TodoItem> todos,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var entries = new List<CalendarEntry>();

        foreach (var todo in todos)
        {
            if (string.IsNullOrWhiteSpace(todo.Recurrence))
            {
                if (todo.DueAt is { } due && due >= from && due <= to)
                {
                    entries.Add(new CalendarEntry(todo, due, IsVirtual: false));
                }

                continue;
            }

            var start = todo.DueAt ?? todo.CreatedAt;
            foreach (var occurrence in RecurrenceEngine.Occurrences(start, todo.Recurrence, from, to))
            {
                var isMaster = todo.DueAt is { } master && IsSameMoment(master, occurrence);
                entries.Add(new CalendarEntry(todo, occurrence, IsVirtual: !isMaster));
            }
        }

        return entries
            .OrderBy(e => e.Occurrence)
            .ThenBy(e => e.Todo.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    private static bool IsSameMoment(DateTimeOffset a, DateTimeOffset b) =>
        Math.Abs((a - b).TotalMinutes) < 1;
}