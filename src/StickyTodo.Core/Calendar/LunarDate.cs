namespace StickyTodo.Core.Calendar;

/// <summary>
/// 农历日期。
/// </summary>
/// <param name="Year">农历年（干支年按立春换年，这里用的是农历年本身）。</param>
/// <param name="Month">农历月，1~12。</param>
/// <param name="Day">农历日，1~30。</param>
/// <param name="IsLeapMonth">这个月是不是闰月。</param>
public readonly record struct LunarDate(int Year, int Month, int Day, bool IsLeapMonth)
{
    public static readonly LunarDate Invalid = new(0, 0, 0, false);

    public bool IsValid => Year > 0;

    private static readonly string[] MonthNames =
    [
        "正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊",
    ];

    private static readonly string[] DayNames =
    [
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十",
    ];

    private static readonly string[] DayNamesShort =
    [
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十",
    ];

    /// <summary>月份写法，闰月带「闰」字，例如「闰六」。</summary>
    public string MonthName =>
        IsValid && Month is >= 1 and <= 12 ? (IsLeapMonth ? "闰" : string.Empty) + MonthNames[Month - 1] : string.Empty;

    /// <summary>日子写法，例如「廿三」。</summary>
    public string DayName => IsValid && Day is >= 1 and <= 30 ? DayNames[Day - 1] : string.Empty;

    public string DayNameShort => IsValid && Day is >= 1 and <= 30 ? DayNamesShort[Day - 1] : string.Empty;

    /// <summary>初一显示月名，其它显示日名——日历格子里就按这个惯例。</summary>
    public string ShortText
    {
        get
        {
            if (!IsValid)
            {
                return string.Empty;
            }

            if (Day == 1)
            {
                return MonthName + "月";
            }

            return DayName;
        }
    }

    public string FullText => IsValid ? $"{MonthName}月{DayName}" : string.Empty;
}