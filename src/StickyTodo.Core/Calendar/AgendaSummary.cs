namespace StickyTodo.Core.Calendar;

/// <summary>某一天的日程概况（桌面时钟的「今日」摘要用）。</summary>
public sealed record AgendaSnapshot(int Total, int Done, CalendarEntry? Next)
{
    /// <summary>还没做完的条数。</summary>
    public int Pending => Total - Done;

    /// <summary>这一天没有任何安排。</summary>
    public bool IsEmpty => Total == 0;

    /// <summary>有安排且全部完成。</summary>
    public bool AllDone => Total > 0 && Done == Total;
}

/// <summary>
/// 从日历投影里算「今天有几项、做完几项、下一项是什么」。
/// 纯函数：不碰数据库、不碰 UI，方便单测。
/// </summary>
public static class AgendaSummary
{
    /// <summary>
    /// 「下一项」= 未完成里第一个时间 &gt;= now 的；今天的时间都过了就退回最早的未完成项；
    /// 全部完成（或今天没有安排）时为 null。
    /// </summary>
    public static AgendaSnapshot Build(IEnumerable<CalendarEntry> entries, DateOnly day, DateTimeOffset now)
    {
        var today = entries.Where(e => e.Day == day).ToList();
        var done = today.Count(e => e.Todo.IsDone);

        var pending = today
            .Where(e => !e.Todo.IsDone)
            .OrderBy(e => e.Occurrence)
            .ToList();

        var next = pending.FirstOrDefault(e => e.Occurrence >= now) ?? pending.FirstOrDefault();

        return new AgendaSnapshot(today.Count, done, next);
    }
}
