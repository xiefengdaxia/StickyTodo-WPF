namespace StickyTodo.Core.WorldClock;

/// <summary>
/// 全球时区代表城市表：方案 B —— 32 个标准时偏移，从西（UTC−11）到东（UTC+14），
/// 每个偏移一个代表城市，含 +3:30 / +4:30 / +5:30 / +5:45 这类非整点偏移。
///
/// 取舍说明：
///   * 去掉 UTC−12（贝克岛，无常住人口）、−9:30（马克萨斯）、+6:30（仰光/科科斯）、
///     +8:45（尤克拉）、+10:30（豪勋爵岛）、+12:45（查塔姆）；
///   * 保留所有整点偏移 + 有实际人口的 30/45 分钟偏移；
///   * 有夏令时的城市在夏天会自动 +1（见 <see cref="WorldClockTime"/>）。
/// </summary>
public static class WorldClockCatalog
{
    /// <summary>按标准时偏移升序（UTC−11 → UTC+14）。</summary>
    public static IReadOnlyList<WorldClockCity> All { get; } =
    [
        new("美属萨摩亚", "帕果帕果", new TimeSpan(-11, 0, 0)),
        new("美国", "火奴鲁鲁", new TimeSpan(-10, 0, 0)),
        new("美国", "安克雷奇", new TimeSpan(-9, 0, 0), DaylightRule.UnitedStates),
        new("美国", "洛杉矶", new TimeSpan(-8, 0, 0), DaylightRule.UnitedStates),
        new("美国", "丹佛", new TimeSpan(-7, 0, 0), DaylightRule.UnitedStates),
        new("美国", "芝加哥", new TimeSpan(-6, 0, 0), DaylightRule.UnitedStates),
        new("美国", "纽约", new TimeSpan(-5, 0, 0), DaylightRule.UnitedStates),
        new("智利", "圣地亚哥", new TimeSpan(-4, 0, 0), DaylightRule.Chile),
        new("加拿大", "圣约翰斯", TimeSpan.FromMinutes(-210), DaylightRule.UnitedStates),
        new("巴西", "圣保罗", new TimeSpan(-3, 0, 0)),
        new("巴西", "费尔南多-迪诺罗尼亚", new TimeSpan(-2, 0, 0)),
        new("葡萄牙", "蓬塔德尔加达", new TimeSpan(-1, 0, 0), DaylightRule.Europe),
        new("英国", "伦敦", TimeSpan.Zero, DaylightRule.Europe),
        new("法国", "巴黎", new TimeSpan(1, 0, 0), DaylightRule.Europe),
        new("埃及", "开罗", new TimeSpan(2, 0, 0), DaylightRule.Egypt),
        new("俄罗斯", "莫斯科", new TimeSpan(3, 0, 0)),
        new("伊朗", "德黑兰", new TimeSpan(3, 30, 0)),
        new("阿联酋", "迪拜", new TimeSpan(4, 0, 0)),
        new("阿富汗", "喀布尔", new TimeSpan(4, 30, 0)),
        new("巴基斯坦", "卡拉奇", new TimeSpan(5, 0, 0)),
        new("印度", "新德里", new TimeSpan(5, 30, 0)),
        new("尼泊尔", "加德满都", new TimeSpan(5, 45, 0)),
        new("孟加拉国", "达卡", new TimeSpan(6, 0, 0)),
        new("泰国", "曼谷", new TimeSpan(7, 0, 0)),
        new("中国", "北京", new TimeSpan(8, 0, 0)),
        new("日本", "东京", new TimeSpan(9, 0, 0)),
        new("澳大利亚", "阿德莱德", new TimeSpan(9, 30, 0), DaylightRule.AustraliaSouth),
        new("澳大利亚", "悉尼", new TimeSpan(10, 0, 0), DaylightRule.AustraliaSouth),
        new("所罗门群岛", "霍尼亚拉", new TimeSpan(11, 0, 0)),
        new("新西兰", "奥克兰", new TimeSpan(12, 0, 0), DaylightRule.NewZealand),
        new("汤加", "努库阿洛法", new TimeSpan(13, 0, 0)),
        new("基里巴斯", "圣诞岛", new TimeSpan(14, 0, 0)),
    ];
}
