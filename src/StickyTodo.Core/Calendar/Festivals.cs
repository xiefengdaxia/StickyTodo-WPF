namespace StickyTodo.Core.Calendar;

/// <summary>
/// 农历节日与传统节日。这些日子按农历固定（或按公历固定），
/// 和国务院每年发布的"放假安排"无关，所以单独一份，任何年份都能标出来。
/// </summary>
public static class Festivals
{
    /// <summary>按农历固定的节日：(月, 日) → 名称。</summary>
    private static readonly Dictionary<(int Month, int Day), string> LunarFixed = new()
    {
        [(1, 1)] = "春节",
        [(1, 15)] = "元宵",
        [(2, 2)] = "龙抬头",
        [(5, 5)] = "端午",
        [(7, 7)] = "七夕",
        [(7, 15)] = "中元",
        [(8, 15)] = "中秋",
        [(9, 9)] = "重阳",
        [(12, 8)] = "腊八",
    };

    /// <summary>按公历固定的节日：(月, 日) → 名称。</summary>
    private static readonly Dictionary<(int Month, int Day), string> SolarFixed = new()
    {
        [(1, 1)] = "元旦",
        [(2, 14)] = "情人节",
        [(3, 8)] = "妇女节",
        [(5, 1)] = "劳动节",
        [(5, 4)] = "青年节",
        [(6, 1)] = "儿童节",
        [(7, 1)] = "建党节",
        [(8, 1)] = "建军节",
        [(9, 10)] = "教师节",
        [(10, 1)] = "国庆节",
        [(12, 25)] = "圣诞节",
    };

    /// <summary>
    /// 这一天是什么传统节日。优先级：除夕 &gt; 农历节日 &gt; 公历节日。
    /// </summary>
    public static string? NameOn(DateOnly date)
    {
        if (LunarCalendar.IsLunarNewYearEve(date))
        {
            return "除夕";
        }

        var lunar = LunarCalendar.SolarToLunar(date);
        if (lunar.IsValid && !lunar.IsLeapMonth && LunarFixed.TryGetValue((lunar.Month, lunar.Day), out var lunarName))
        {
            return lunarName;
        }

        // 公历节日若正好不在农历节日上，就用公历的
        return SolarFixed.TryGetValue((date.Month, date.Day), out var solarName) ? solarName : null;
    }
}