namespace StickyTodo.Core.Calendar;

/// <summary>月视图里的一格。</summary>
public readonly record struct MonthCell(DateOnly Date, bool IsCurrentMonth)
{
    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}

/// <summary>
/// 月视图网格：固定 6 行 × 7 列，跨月补齐。纯计算，便于单测。
/// </summary>
public static class MonthGrid
{
    public const int WeeksShown = 6;
    public const int DaysShown = WeeksShown * 7;

    public static IReadOnlyList<MonthCell> Build(int year, int month, DayOfWeek firstDayOfWeek = DayOfWeek.Monday)
    {
        var first = new DateOnly(year, month, 1);
        var offset = (((int)first.DayOfWeek - (int)firstDayOfWeek) + 7) % 7;
        var start = first.AddDays(-offset);

        return Enumerable.Range(0, DaysShown)
            .Select(i =>
            {
                var date = start.AddDays(i);
                return new MonthCell(date, date.Year == year && date.Month == month);
            })
            .ToList();
    }

    /// <summary>整个网格覆盖的日期区间（含补出来的前后几天）。</summary>
    public static (DateOnly From, DateOnly To) VisibleRange(int year, int month, DayOfWeek firstDayOfWeek = DayOfWeek.Monday)
    {
        var cells = Build(year, month, firstDayOfWeek);
        return (cells[0].Date, cells[^1].Date);
    }

    /// <summary>月份偏移，自动跨年。</summary>
    public static (int Year, int Month) Shift(int year, int month, int deltaMonths)
    {
        var index = (year * 12) + (month - 1) + deltaMonths;
        return (index / 12, (index % 12) + 1);
    }
}