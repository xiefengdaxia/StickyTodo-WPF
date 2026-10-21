namespace StickyTodo.Core.Calendar;

/// <summary>
/// 二十四节气。
/// 21 世纪（2000–2099）用通用日数公式：
///   日 = floor(Y × 0.2422 + C) − L
/// 其中 Y 是年份后两位，C 是各节气常数，L 对小寒/大寒/立春/雨水取 floor((Y−1)/4)、
/// 其余取 floor(Y/4)。这套公式在本世纪内与官方历书一致（个别年份有特例，见下）。
/// 2000 年之前退化为按回归年线性外推，只用于兜底展示。
/// </summary>
public static class SolarTerms
{
    /// <summary>从「小寒」开始按时间顺序排列的 24 个节气名。</summary>
    public static readonly string[] Names =
    [
        "小寒", "大寒", "立春", "雨水", "惊蛰", "春分", "清明", "谷雨",
        "立夏", "小满", "芒种", "夏至", "小暑", "大暑", "立秋", "处暑",
        "白露", "秋分", "寒露", "霜降", "立冬", "小雪", "大雪", "冬至",
    ];

    /// <summary>21 世纪各节气的 C 常数。</summary>
    private static readonly double[] CenturyC =
    [
        5.4055, 20.12, 3.87, 18.73, 5.63, 20.646, 4.81, 20.1,
        5.52, 21.04, 5.678, 21.37, 7.108, 22.83, 7.5, 23.13,
        7.646, 23.042, 8.318, 23.438, 7.438, 22.36, 7.18, 21.94,
    ];

    /// <summary>已知需要 +1 天的特例（年份, 节气下标）与 −1 天的特例。</summary>
    private static readonly HashSet<(int Year, int Index)> PlusOne =
    [
        (2026, 3), // 雨水
    ];

    private static readonly HashSet<(int Year, int Index)> MinusOne = [];

    // 兜底用的线性近似（1900 年小寒为基准）
    private static readonly int[] MinuteOffsets =
    [
        0, 21208, 42467, 63836, 85337, 107014, 128867, 150921,
        173149, 195551, 218072, 240693, 263343, 285989, 308563, 331033,
        353350, 375494, 397447, 419210, 440795, 462224, 483532, 504758,
    ];

    private const double TropicalYearMs = 31556925974.7;

    private static readonly DateTime LinearBase = new(1900, 1, 6, 2, 5, 0, DateTimeKind.Unspecified);

    /// <summary>某年第 index 个节气（0 = 小寒）落在哪一天。</summary>
    public static DateOnly DateOf(int year, int index)
    {
        if (index is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var month = (index / 2) + 1;

        if (year is >= 2000 and <= 2099)
        {
            var y = year % 100;
            var l = index <= 3 ? (y - 1) / 4 : y / 4;
            var day = (int)Math.Floor((y * 0.2422) + CenturyC[index]) - l;

            if (PlusOne.Contains((year, index)))
            {
                day++;
            }

            if (MinusOne.Contains((year, index)))
            {
                day--;
            }

            return new DateOnly(year, month, Math.Clamp(day, 1, 31));
        }

        // 兜底：线性近似
        var ms = (TropicalYearMs * (year - 1900)) + (MinuteOffsets[index] * 60000d);
        return DateOnly.FromDateTime(LinearBase.AddMilliseconds(ms));
    }

    /// <summary>某年 24 个节气的日期，按时间顺序。</summary>
    public static IReadOnlyList<(string Name, DateOnly Date)> Of(int year)
    {
        var list = new List<(string, DateOnly)>(24);
        for (var i = 0; i < 24; i++)
        {
            list.Add((Names[i], DateOf(year, i)));
        }

        return list;
    }

    /// <summary>这一天是不是节气；是则返回节气名。</summary>
    public static string? NameOn(DateOnly date)
    {
        if (date.Year is < 1901 or > 2099)
        {
            return null;
        }

        for (var i = 0; i < 24; i++)
        {
            if (DateOf(date.Year, i) == date)
            {
                return Names[i];
            }
        }

        return null;
    }

    /// <summary>
    /// 这一天的「月建」地支序号（0=子, 1=丑, 2=寅 …）。
    /// 以「节」为界：立春进寅月、惊蛰进卯月、清明进辰月……
    /// </summary>
    public static int MonthBranchIndex(DateOnly date)
    {
        var year = date.Year;

        // 从本年大雪往回找最近一个「节」（偶数下标的节气）
        for (var i = 22; i >= 0; i -= 2)
        {
            if (date >= DateOf(year, i))
            {
                return (1 + (i / 2)) % 12;
            }
        }

        // 比本年的小寒还早 → 还在上一年的子月（大雪之后）
        return 0;
    }
}