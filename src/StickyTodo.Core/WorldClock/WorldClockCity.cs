namespace StickyTodo.Core.WorldClock;

/// <summary>
/// 夏令时规则。
///
/// 刻意不用 <c>TimeZoneInfo</c>：Win7 自带的时区数据是 2011 年的，
/// 俄罗斯/土耳其/巴西/墨西哥/伊朗/埃及/斐济 这些后来改过规则的城市会算错，
/// 而世界时钟只要"现在的钟点对不对"，所以这里内置规则、跨系统一致。
/// </summary>
public enum DaylightRule
{
    /// <summary>全年固定偏移，没有夏令时。</summary>
    None,

    /// <summary>美国/加拿大：3 月第 2 个周日 02:00 → 11 月第 1 个周日 02:00（本地标准时）。</summary>
    UnitedStates,

    /// <summary>欧洲：3 月最后周日 01:00 UTC → 10 月最后周日 01:00 UTC。</summary>
    Europe,

    /// <summary>智利：9 月第 1 个周日 → 次年 4 月第 1 个周日（本地 00:00）。</summary>
    Chile,

    /// <summary>埃及：4 月最后一个周五 → 10 月最后一个周五（本地 00:00）。</summary>
    Egypt,

    /// <summary>澳大利亚南部：10 月第 1 个周日 → 次年 4 月第 1 个周日（本地 02:00 标准时）。</summary>
    AustraliaSouth,

    /// <summary>新西兰：9 月最后周日 → 次年 4 月第 1 个周日（本地 02:00 标准时）。</summary>
    NewZealand,
}

/// <summary>世界时钟里的一个城市（一个标准时偏移只放一个代表城市）。</summary>
/// <param name="Country">国家/地区名（中文）。</param>
/// <param name="City">城市名（中文）。</param>
/// <param name="StandardOffset">标准时（冬令时）偏移。</param>
/// <param name="Rule">夏令时规则。</param>
public sealed record WorldClockCity(
    string Country,
    string City,
    TimeSpan StandardOffset,
    DaylightRule Rule = DaylightRule.None)
{
    /// <summary>这个城市有没有夏令时。</summary>
    public bool ObservesDaylightSaving => Rule != DaylightRule.None;

    /// <summary>标准时标签，例如 <c>UTC+8</c> / <c>UTC-3:30</c>。</summary>
    public string StandardOffsetLabel => "UTC" + WorldClockTime.FormatOffset(StandardOffset);
}
