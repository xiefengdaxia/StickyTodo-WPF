namespace StickyTodo.Core.Calendar;

/// <summary>某一天的性质。</summary>
public enum DayKind
{
    /// <summary>普通工作日。</summary>
    Workday = 0,

    /// <summary>周末（周六周日，但不是补班）。</summary>
    Weekend = 1,

    /// <summary>法定节假日或调休放假。</summary>
    Holiday = 2,

    /// <summary>调休补班（本来是周末，但要上班）。</summary>
    MakeupWorkday = 3,
}

/// <summary>中国法定节假日安排里的一条：一整段假期，或若干个补班日。</summary>
public sealed record HolidaySpan(string Name, DateOnly From, DateOnly To);

/// <summary>
/// 中国法定节假日与调休安排。
/// 数据来自国务院办公厅每年发布的通知（2024 年国办发明电〔2023〕7 号、
/// 2025 年〔2024〕12 号、2026 年〔2025〕7 号），手工录入，不联网。
/// 超出已收录年份时退化成"周末休息、工作日上班"。
/// 内置数据可以用 <see cref="SetImported"/> 导入的数据覆盖（AI 通过 MCP 写入），
/// 所以这里所有查询都是"先看导入，再看内置"。
/// </summary>
public static class ChineseHolidays
{
    /// <summary>收录的最早年份。</summary>
    public const int MinYear = 2024;

    /// <summary>收录的最晚年份。新一年的通知发布后在这里补数据即可。</summary>
    public const int MaxYear = 2026;

    private static readonly HolidaySpan[] Spans =
    [
        // ---------- 2024 ----------
        new("元旦", new DateOnly(2023, 12, 30), new DateOnly(2024, 1, 1)),
        new("春节", new DateOnly(2024, 2, 10), new DateOnly(2024, 2, 17)),
        new("清明", new DateOnly(2024, 4, 4), new DateOnly(2024, 4, 6)),
        new("劳动节", new DateOnly(2024, 5, 1), new DateOnly(2024, 5, 5)),
        new("端午", new DateOnly(2024, 6, 8), new DateOnly(2024, 6, 10)),
        new("中秋", new DateOnly(2024, 9, 15), new DateOnly(2024, 9, 17)),
        new("国庆", new DateOnly(2024, 10, 1), new DateOnly(2024, 10, 7)),

        // ---------- 2025 ----------
        new("元旦", new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 1)),
        new("春节", new DateOnly(2025, 1, 28), new DateOnly(2025, 2, 4)),
        new("清明", new DateOnly(2025, 4, 4), new DateOnly(2025, 4, 6)),
        new("劳动节", new DateOnly(2025, 5, 1), new DateOnly(2025, 5, 5)),
        new("端午", new DateOnly(2025, 5, 31), new DateOnly(2025, 6, 2)),
        new("国庆·中秋", new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 8)),

        // ---------- 2026 ----------
        new("元旦", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3)),
        new("春节", new DateOnly(2026, 2, 15), new DateOnly(2026, 2, 23)),
        new("清明", new DateOnly(2026, 4, 4), new DateOnly(2026, 4, 6)),
        new("劳动节", new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 5)),
        new("端午", new DateOnly(2026, 6, 19), new DateOnly(2026, 6, 21)),
        new("中秋", new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 27)),
        new("国庆", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7)),
    ];

    private static readonly DateOnly[] MakeupDays =
    [
        // 2024
        new(2024, 2, 4), new(2024, 2, 18), new(2024, 4, 7),
        new(2024, 4, 28), new(2024, 5, 11), new(2024, 9, 14),
        new(2024, 9, 29), new(2024, 10, 12),

        // 2025
        new(2025, 1, 26), new(2025, 2, 8), new(2025, 4, 27),
        new(2025, 9, 28), new(2025, 10, 11),

        // 2026
        new(2026, 1, 4), new(2026, 2, 14), new(2026, 2, 28),
        new(2026, 5, 9), new(2026, 9, 20), new(2026, 10, 10),
    ];

    private static readonly HashSet<DateOnly> MakeupSet = MakeupDays.ToHashSet();

    /// <summary>
    /// 导入的数据（AI 通过 MCP 的 import_holidays 写入，落在设置键 calendar.holidays）。
    /// 非空时优先于上面那两张内置表：同一天命中导入区间，就按导入的 kind 算。
    /// 整体替换（不是叠加），所以换一份数据只要再调一次 <see cref="SetImported"/>。
    /// </summary>
    private static volatile ImportedHoliday[] _imported = [];

    /// <summary>当前生效的导入数据。</summary>
    public static IReadOnlyList<ImportedHoliday> Imported => _imported;

    /// <summary>换一份导入数据；传 null / 空集合等于清空，回到内置数据。</summary>
    public static void SetImported(IEnumerable<ImportedHoliday>? entries) =>
        _imported = entries?.Where(entry => entry is not null).ToArray() ?? [];

    /// <summary>导入数据里这一天是什么性质；这一天没被导入覆盖就返回 null（回落到内置数据）。</summary>
    private static DayKind? ImportedKindOf(DateOnly date)
    {
        foreach (var entry in _imported)
        {
            if (entry.Covers(date))
            {
                return entry.Kind;
            }
        }

        return null;
    }

    /// <summary>这个日期是不是调休补班日。</summary>
    public static bool IsMakeupWorkday(DateOnly date) =>
        ImportedKindOf(date) is { } imported ? imported == DayKind.MakeupWorkday : MakeupSet.Contains(date);

    /// <summary>这个日期落在哪段假期里，不在假期则返回 null。导入的假期优先。</summary>
    public static HolidaySpan? FindSpan(DateOnly date)
    {
        foreach (var entry in _imported)
        {
            if (!entry.Covers(date))
            {
                continue;
            }

            // 被导入成补班/工作日的日期不算假期（覆盖内置里同名的那一段）
            return entry.Kind == DayKind.Holiday ? new HolidaySpan(entry.Name, entry.From, entry.End) : null;
        }

        foreach (var span in Spans)
        {
            if (date >= span.From && date <= span.To)
            {
                return span;
            }
        }

        return null;
    }

    /// <summary>这一天的性质。补班优先于周末，假期优先于补班；导入的数据压过内置的。</summary>
    public static DayKind KindOf(DateOnly date)
    {
        if (ImportedKindOf(date) is { } imported)
        {
            return imported;
        }

        if (IsMakeupWorkday(date))
        {
            return DayKind.MakeupWorkday;
        }

        if (FindSpan(date) is not null)
        {
            return DayKind.Holiday;
        }

        return date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? DayKind.Weekend
            : DayKind.Workday;
    }

    /// <summary>
    /// 某一年的全部安排，一天一条（date / name / kind / 是否来自导入），
    /// 给 MCP 的 get_holidays 用。同一天既有内置又有导入时只留导入的那条。
    /// </summary>
    public static IReadOnlyList<HolidayEntry> EntriesOf(int year)
    {
        var entries = new List<HolidayEntry>();

        foreach (var span in Spans)
        {
            for (var day = span.From; day <= span.To; day = day.AddDays(1))
            {
                if (day.Year == year)
                {
                    entries.Add(new HolidayEntry(day, span.Name, DayKind.Holiday, Imported: false));
                }
            }
        }

        foreach (var day in MakeupDays)
        {
            if (day.Year == year)
            {
                entries.Add(new HolidayEntry(day, "补班", DayKind.MakeupWorkday, Imported: false));
            }
        }

        foreach (var entry in _imported)
        {
            for (var day = entry.From; day <= entry.End; day = day.AddDays(1))
            {
                if (day.Year == year)
                {
                    entries.Add(new HolidayEntry(day, entry.Name, entry.Kind, Imported: true));
                }
            }
        }

        return entries
            .GroupBy(entry => entry.Date)
            .Select(group => group.OrderByDescending(entry => entry.Imported).First())
            .OrderBy(entry => entry.Date)
            .ToList();
    }

    /// <summary>
    /// 日历格子里那句提示：
    /// 节假日显示「休」+ 名称，补班显示「班」，周末显示空。
    /// </summary>
    public static string BadgeOf(DateOnly date) => KindOf(date) switch
    {
        DayKind.Holiday => "休",
        DayKind.MakeupWorkday => "班",
        _ => string.Empty,
    };

    /// <summary>假期的名字；不是假期返回 null。</summary>
    public static string? HolidayNameOf(DateOnly date) => FindSpan(date)?.Name;

    /// <summary>
    /// 日历格子里要显示的短标签，优先级：
    /// 春节段里的除夕 → 「除夕」；其它假期 → 假期名；节气由上层单独判断。
    /// </summary>
    public static string? ShortLabelOf(DateOnly date)
    {
        if (LunarCalendar.IsLunarNewYearEve(date))
        {
            return "除夕";
        }

        return HolidayNameOf(date);
    }

    /// <summary>是否已收录这一年的安排（内置年份，或导入数据盖到了这一年）。</summary>
    public static bool HasYear(int year) =>
        year is >= MinYear and <= MaxYear || _imported.Any(entry => entry.From.Year <= year && entry.End.Year >= year);

    /// <summary>收录了哪些年份，给界面提示用。</summary>
    public static string CoverageText => _imported.Length > 0
        ? $"{MinYear}–{MaxYear} 年（另有导入 {_imported.Length} 段）"
        : $"{MinYear}–{MaxYear} 年";
}