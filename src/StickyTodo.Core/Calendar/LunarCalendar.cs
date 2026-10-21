namespace StickyTodo.Core.Calendar;

/// <summary>
/// 公历 ↔ 农历换算。算法用 1900–2100 年的农历月份长度表（每一年一个整数，
/// 记录闰月月份和 13 个月的大小月），这是公开的历法数据，不含任何第三方代码。
/// </summary>
public static class LunarCalendar
{
    // 每年一个 16 进制数：
    //   低 4 位      = 闰哪个月（0 表示没有闰月）
    //   0x10000 位   = 闰月是大月(30)还是小月(29)
    //   其余 12 位   = 正月到腊月的大小月（1=30 天，0=29 天）
    private static readonly int[] LunarInfo =
    [
        0x04bd8, 0x04ae0, 0x0a570, 0x054d5, 0x0d260, 0x0d950, 0x16554, 0x056a0, 0x09ad0, 0x055d2,
        0x04ae0, 0x0a5b6, 0x0a4d0, 0x0d250, 0x1d255, 0x0b540, 0x0d6a0, 0x0ada2, 0x095b0, 0x14977,
        0x04970, 0x0a4b0, 0x0b4b5, 0x06a50, 0x06d40, 0x1ab54, 0x02b60, 0x09570, 0x052f2, 0x04970,
        0x06566, 0x0d4a0, 0x0ea50, 0x06e95, 0x05ad0, 0x02b60, 0x186e3, 0x092e0, 0x1c8d7, 0x0c950,
        0x0d4a0, 0x1d8a6, 0x0b550, 0x056a0, 0x1a5b4, 0x025d0, 0x092d0, 0x0d2b2, 0x0a950, 0x0b557,
        0x06ca0, 0x0b550, 0x15355, 0x04da0, 0x0a5b0, 0x14573, 0x052b0, 0x0a9a8, 0x0e950, 0x06aa0,
        0x0aea6, 0x0ab50, 0x04b60, 0x0aae4, 0x0a570, 0x05260, 0x0f263, 0x0d950, 0x05b57, 0x056a0,
        0x096d0, 0x04dd5, 0x04ad0, 0x0a4d0, 0x0d4d4, 0x0d250, 0x0d558, 0x0b540, 0x0b5a0, 0x195a6,
        0x095b0, 0x049b0, 0x0a974, 0x0a4b0, 0x0b27a, 0x06a50, 0x06d40, 0x0af46, 0x0ab60, 0x09570,
        0x04af5, 0x04970, 0x064b0, 0x074a3, 0x0ea50, 0x06b58, 0x055c0, 0x0ab60, 0x096d5, 0x092e0,
        0x0c960, 0x0d954, 0x0d4a0, 0x0da50, 0x07552, 0x056a0, 0x0abb7, 0x025d0, 0x092d0, 0x0cab5,
        0x0a950, 0x0b4a0, 0x0baa4, 0x0ad50, 0x055d9, 0x04ba0, 0x0a5b0, 0x15176, 0x052b0, 0x0a930,
        0x07954, 0x06aa0, 0x0ad50, 0x05b52, 0x04b60, 0x0a6e6, 0x0a4e0, 0x0d260, 0x0ea65, 0x0d530,
        0x05aa0, 0x076a3, 0x096d0, 0x04afb, 0x04ad0, 0x0a4d0, 0x1d0b6, 0x0d250, 0x0d520, 0x0dd45,
        0x0b5a0, 0x056d0, 0x055b2, 0x049b0, 0x0a577, 0x0a4b0, 0x0aa50, 0x1b255, 0x06d20, 0x0ada0,
        0x14b63, 0x09370, 0x049f8, 0x04970, 0x064b0, 0x168a6, 0x0ea50, 0x06b20, 0x1a6c4, 0x0aae0,
        0x0a2e0, 0x0d2e3, 0x0c960, 0x0d557, 0x0d4a0, 0x0da50, 0x05d55, 0x056a0, 0x0a6d0, 0x055d4,
        0x052d0, 0x0a9b8, 0x0a950, 0x0b4a0, 0x0b6a6, 0x0ad50, 0x055a0, 0x0aba4, 0x0a5b0, 0x052b0,
        0x0b273, 0x06930, 0x07337, 0x06aa0, 0x0ad50, 0x14b55, 0x04b60, 0x0a570, 0x054e4, 0x0d160,
        0x0e968, 0x0d520, 0x0daa0, 0x16aa6, 0x056d0, 0x04ae0, 0x0a9d4, 0x0a2d0, 0x0d150, 0x0f252,
        0x0d520,
    ];

    private const int MinYear = 1900;
    private const int MaxYear = 2100;

    /// <summary>农历 1900 年正月初一对应的公历日期。</summary>
    private static readonly DateTime BaseDate = new(1900, 1, 31);

    public static readonly string[] HeavenlyStems = ["甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸"];

    public static readonly string[] EarthlyBranches = ["子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥"];

    public static readonly string[] Zodiacs = ["鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪"];

    public static bool IsSupported(int year) => year is >= MinYear and <= MaxYear;

    /// <summary>某农历年闰哪个月，0 表示没有闰月。</summary>
    public static int LeapMonth(int lunarYear) =>
        IsSupported(lunarYear) ? LunarInfo[lunarYear - MinYear] & 0xF : 0;

    /// <summary>某农历年某个月有多少天（29 或 30）。</summary>
    public static int MonthDays(int lunarYear, int month)
    {
        if (!IsSupported(lunarYear) || month is < 1 or > 12)
        {
            return 29;
        }

        return (LunarInfo[lunarYear - MinYear] & (0x10000 >> month)) != 0 ? 30 : 29;
    }

    /// <summary>闰月有多少天。</summary>
    private static int LeapMonthDays(int lunarYear)
    {
        var leap = LeapMonth(lunarYear);
        if (leap == 0)
        {
            return 0;
        }

        return (LunarInfo[lunarYear - MinYear] & 0x10000) != 0 ? 30 : 29;
    }

    /// <summary>某农历年一共多少天。</summary>
    public static int YearDays(int lunarYear)
    {
        if (!IsSupported(lunarYear))
        {
            return 354;
        }

        var sum = 0;
        for (var month = 1; month <= 12; month++)
        {
            sum += MonthDays(lunarYear, month);
        }

        return sum + LeapMonthDays(lunarYear);
    }

    /// <summary>公历 → 农历。超出 1900–2100 返回 <see cref="LunarDate.Invalid"/>。</summary>
    public static LunarDate SolarToLunar(DateOnly date)
    {
        if (date.Year is < MinYear or > MaxYear)
        {
            return LunarDate.Invalid;
        }

        var offset = date.DayNumber - DateOnly.FromDateTime(BaseDate).DayNumber;
        if (offset < 0)
        {
            return LunarDate.Invalid;
        }

        var lunarYear = MinYear;
        while (lunarYear <= MaxYear)
        {
            var days = YearDays(lunarYear);
            if (offset < days)
            {
                break;
            }

            offset -= days;
            lunarYear++;
        }

        if (lunarYear > MaxYear)
        {
            return LunarDate.Invalid;
        }

        var leap = LeapMonth(lunarYear);
        var isLeap = false;
        var month = 1;

        while (month <= 12)
        {
            var days = isLeap ? LeapMonthDays(lunarYear) : MonthDays(lunarYear, month);
            if (offset < days)
            {
                break;
            }

            offset -= days;

            // 闰月在正常月之后插一段
            if (leap == month)
            {
                if (isLeap)
                {
                    isLeap = false;
                    month++;
                }
                else
                {
                    isLeap = true;
                }
            }
            else
            {
                month++;
            }
        }

        var day = offset + 1;
        return new LunarDate(lunarYear, Math.Min(month, 12), day, isLeap);
    }

    /// <summary>农历 → 公历。找不到返回 null。</summary>
    public static DateOnly? LunarToSolar(LunarDate lunar)
    {
        if (!lunar.IsValid || !IsSupported(lunar.Year))
        {
            return null;
        }

        var offset = 0;
        for (var year = MinYear; year < lunar.Year; year++)
        {
            offset += YearDays(year);
        }

        var leap = LeapMonth(lunar.Year);
        for (var month = 1; month < lunar.Month; month++)
        {
            offset += MonthDays(lunar.Year, month);
            if (leap == month)
            {
                offset += LeapMonthDays(lunar.Year);
            }
        }

        if (lunar.IsLeapMonth && leap == lunar.Month)
        {
            offset += MonthDays(lunar.Year, lunar.Month);
            offset += lunar.Day - 1;
        }
        else if (lunar.IsLeapMonth)
        {
            return null;
        }
        else
        {
            offset += lunar.Day - 1;
        }

        return DateOnly.FromDateTime(BaseDate).AddDays(offset);
    }

    /// <summary>农历年的干支，例如 2026 年返回「丙午」。</summary>
    public static string YearGanZhi(int lunarYear)
    {
        var index = (lunarYear - 4) % 60;
        if (index < 0)
        {
            index += 60;
        }

        return HeavenlyStems[index % 10] + EarthlyBranches[index % 12];
    }

    /// <summary>农历年的生肖。</summary>
    public static string YearZodiac(int lunarYear)
    {
        var index = (lunarYear - 4) % 12;
        if (index < 0)
        {
            index += 12;
        }

        return Zodiacs[index];
    }

    /// <summary>日期的干支（以公历日连续推算，用于黄历）。</summary>
    public static string DayGanZhi(DateOnly date)
    {
        var index = DayGanZhiIndex(date);
        return HeavenlyStems[index % 10] + EarthlyBranches[index % 12];
    }

    /// <summary>日期在六十甲子里的序号（0–59，0 = 甲子）。</summary>
    /// <remarks>
    /// 用儒略日数反推：JDN 60 天一轮，与本历法基准对齐后取模，
    /// 可覆盖 1900–2100 年，且不依赖任何外部数据。
    /// </remarks>
    public static int DayGanZhiIndex(DateOnly date)
    {
        // DayNumber 从 0001-01-01 起算，加 1721425 得儒略日数（正午基准）
        var jdn = date.DayNumber + 1721425;
        return ((jdn + 49) % 60 + 60) % 60;
    }

    /// <summary>是不是除夕（农历腊月最后一天）。</summary>
    public static bool IsLunarNewYearEve(DateOnly date)
    {
        var lunar = SolarToLunar(date);
        if (!lunar.IsValid || lunar.Month != 12)
        {
            return false;
        }

        // 腊月有 29 或 30 天，当天是最后一天即除夕
        var lastDay = MonthDays(lunar.Year, 12);
        return lunar.Day == lastDay;
    }
}