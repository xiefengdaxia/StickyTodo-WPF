namespace StickyTodo.Core.Calendar;

/// <summary>
/// 黄历宜忌。按传统「建除十二神」推算：以月建地支为基准，
/// 日支与月支相同者为「建」，之后依次除、满、平、定、执、破、危、成、收、开、闭，
/// 每一神对应一套宜/忌事项。这是公开的历法规则，不复制任何商业黄历数据。
/// </summary>
public static class Almanac
{
    /// <summary>建除十二神的名称，从「建」开始。</summary>
    public static readonly string[] Officers =
    [
        "建", "除", "满", "平", "定", "执", "破", "危", "成", "收", "开", "闭",
    ];

    /// <summary>十二神各自宜做的事。</summary>
    private static readonly string[][] OfficerYi =
    [
        ["出行", "上任", "会友", "上书", "见工"],
        ["祭祀", "解除", "沐浴", "扫舍", "治病"],
        ["祈福", "嫁娶", "开市", "纳财", "交易"],
        ["修造", "涂泥", "栽种", "安葬", "平治道涂"],
        ["祭祀", "祈福", "嫁娶", "入宅", "安床"],
        ["捕捉", "狩猎", "结网", "造屋", "装修"],
        ["破屋", "坏垣", "求医", "治病", "拆卸"],
        ["祭祀", "安床", "修造", "动土", "上梁"],
        ["开市", "交易", "立券", "纳财", "嫁娶"],
        ["纳财", "收账", "入仓", "捕捉", "进人口"],
        ["开市", "交易", "求职", "入学", "出行"],
        ["安葬", "筑堤", "补垣", "塞穴", "收藏"],
    ];

    /// <summary>十二神各自忌做的事。</summary>
    private static readonly string[][] OfficerJi =
    [
        ["动土", "开仓", "掘井", "安葬", "上梁"],
        ["嫁娶", "开市", "出行", "入宅", "赴任"],
        ["动土", "安葬", "开仓", "掘井", "迁徙"],
        ["开市", "交易", "嫁娶", "入宅", "安葬"],
        ["诉讼", "远行", "开仓", "置产", "针灸"],
        ["开市", "出行", "安葬", "迁徙", "嫁娶"],
        ["嫁娶", "开市", "签约", "出行", "入宅"],
        ["登高", "行船", "远行", "开市", "嫁娶"],
        ["诉讼", "安葬", "破土", "伐木", "行船"],
        ["开市", "安葬", "破土", "出行", "嫁娶"],
        ["安葬", "破土", "伐木", "针灸", "行船"],
        ["开市", "出行", "嫁娶", "入宅", "求医"],
    ];

    /// <summary>这一天的建除十二神（返回序号 0–11 与名称）。</summary>
    public static (int Index, string Name) OfficerOf(DateOnly date)
    {
        var monthBranch = SolarTerms.MonthBranchIndex(date);
        var dayBranch = LunarCalendar.DayGanZhiIndex(date) % 12;

        // 日支 - 月支（都按子=0 排），模 12 即得建除序号
        var index = ((dayBranch - monthBranch) % 12 + 12) % 12;
        return (index, Officers[index]);
    }

    /// <summary>这一天宜做的事。</summary>
    public static IReadOnlyList<string> YiOf(DateOnly date) => OfficerYi[OfficerOf(date).Index];

    /// <summary>这一天忌做的事。</summary>
    public static IReadOnlyList<string> JiOf(DateOnly date) => OfficerJi[OfficerOf(date).Index];

    /// <summary>把一天的黄历信息整理好。</summary>
    public static AlmanacDay Describe(DateOnly date)
    {
        var lunar = LunarCalendar.SolarToLunar(date);
        var (index, name) = OfficerOf(date);

        return new AlmanacDay(
            date,
            lunar,
            LunarCalendar.DayGanZhi(date),
            name,
            OfficerYi[index],
            OfficerJi[index],
            SolarTerms.NameOn(date),
            ChineseHolidays.HolidayNameOf(date),
            ChineseHolidays.KindOf(date));
    }
}

/// <summary>一天的黄历信息。</summary>
public sealed record AlmanacDay(
    DateOnly Date,
    LunarDate Lunar,
    string GanZhi,
    string Officer,
    IReadOnlyList<string> Yi,
    IReadOnlyList<string> Ji,
    string? SolarTerm,
    string? HolidayName,
    DayKind Kind);