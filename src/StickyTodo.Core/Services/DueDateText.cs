namespace StickyTodo.Core.Services;

/// <summary>
/// 倒数日文案。纯函数，便于单测；界面只负责显示。
/// </summary>
public static class DueDateText
{
    /// <summary>如「今天到期」「还剩 3 天」「已过期 2 天」；没有目标日期返回空串。</summary>
    public static string Describe(DateTimeOffset? due, DateTimeOffset now)
    {
        if (due is null)
        {
            return string.Empty;
        }

        var days = DaysBetween(due.Value, now);

        return days switch
        {
            0 => "今天到期",
            1 => "明天到期",
            -1 => "昨天到期",
            > 1 => $"还剩 {days} 天",
            _ => $"已过期 {-days} 天",
        };
    }

    /// <summary>是否已经过了目标日期（按自然日算）。</summary>
    public static bool IsOverdue(DateTimeOffset? due, DateTimeOffset now) =>
        due is not null && DaysBetween(due.Value, now) < 0;

    /// <summary>目标日期与今天的自然日差（正数=未来）。</summary>
    public static int DaysBetween(DateTimeOffset due, DateTimeOffset now) =>
        (due.Date - now.Date).Days;
}