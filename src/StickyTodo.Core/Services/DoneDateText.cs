namespace StickyTodo.Core.Services;

/// <summary>
/// 完成时间的显示文案（纯函数，便于单测）。
/// 规则：今天/昨天用相对说法，同年省略年份，跨年带年份。
/// </summary>
public static class DoneDateText
{
    /// <summary>如「今天 14:32」「昨天 09:05」「10月18日 20:11」「2025年12月30日 08:00」；无完成时间返回空串。</summary>
    public static string Describe(DateTimeOffset? doneAt, DateTimeOffset now)
    {
        if (doneAt is null)
        {
            return string.Empty;
        }

        var done = doneAt.Value;
        var days = (now.Date - done.Date).Days;

        if (days == 0)
        {
            return $"今天 {done:HH:mm}";
        }

        if (days == 1)
        {
            return $"昨天 {done:HH:mm}";
        }

        return done.Year == now.Year
            ? $"{done:M月d日 HH:mm}"
            : $"{done:yyyy年M月d日 HH:mm}";
    }
}