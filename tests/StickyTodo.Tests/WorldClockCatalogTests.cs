using StickyTodo.Core.WorldClock;

namespace StickyTodo.Tests;

public sealed class WorldClockCatalogTests
{
    private static readonly DateTime Winter = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Summer = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    // ---------------- 表本身 ----------------

    [Fact]
    public void 方案B_共32个时区且从西到东严格递增()
    {
        var all = WorldClockCatalog.All;

        Assert.Equal(32, all.Count);
        Assert.Equal(TimeSpan.FromHours(-11), all[0].StandardOffset);
        Assert.Equal(TimeSpan.FromHours(14), all[^1].StandardOffset);

        for (var i = 1; i < all.Count; i++)
        {
            Assert.True(
                all[i].StandardOffset > all[i - 1].StandardOffset,
                $"第 {i} 条 {all[i].City} 的偏移没有比前一条大");
        }
    }

    [Fact]
    public void 每个时区只放一个代表城市且字段齐全()
    {
        var offsets = WorldClockCatalog.All.Select(c => c.StandardOffset).ToList();

        Assert.Equal(offsets.Count, offsets.Distinct().Count());
        Assert.All(WorldClockCatalog.All, city =>
        {
            Assert.False(string.IsNullOrWhiteSpace(city.Country));
            Assert.False(string.IsNullOrWhiteSpace(city.City));
            Assert.InRange(city.StandardOffset, TimeSpan.FromHours(-12), TimeSpan.FromHours(14));
        });
    }

    [Theory]
    [InlineData(0, "+0")]
    [InlineData(8, "+8")]
    [InlineData(-5, "-5")]
    [InlineData(14, "+14")]
    [InlineData(-11, "-11")]
    public void 整点偏移格式(int hours, string expected)
    {
        Assert.Equal(expected, WorldClockTime.FormatOffset(TimeSpan.FromHours(hours)));
    }

    [Theory]
    [InlineData(5, 30, "+5:30")]
    [InlineData(5, 45, "+5:45")]
    [InlineData(3, 30, "+3:30")]
    [InlineData(4, 30, "+4:30")]
    public void 半点偏移格式(int hours, int minutes, string expected)
    {
        Assert.Equal(expected, WorldClockTime.FormatOffset(new TimeSpan(hours, minutes, 0)));
    }

    [Fact]
    public void 负半点偏移格式()
    {
        Assert.Equal("-3:30", WorldClockTime.FormatOffset(TimeSpan.FromMinutes(-210)));
        Assert.Equal("UTC+8", WorldClockTime.FormatOffsetLabel(TimeSpan.FromHours(8)));
    }

    // ---------------- 夏令时 ----------------

    [Theory]
    // 城市, 冬天偏移, 夏天偏移
    [InlineData("洛杉矶", -8, -7)]
    [InlineData("丹佛", -7, -6)]
    [InlineData("芝加哥", -6, -5)]
    [InlineData("纽约", -5, -4)]
    [InlineData("安克雷奇", -9, -8)]
    [InlineData("伦敦", 0, 1)]
    [InlineData("巴黎", 1, 2)]
    [InlineData("蓬塔德尔加达", -1, 0)]
    [InlineData("开罗", 2, 3)]
    [InlineData("圣地亚哥", -3, -4)]
    [InlineData("悉尼", 11, 10)]
    [InlineData("阿德莱德", 10.5, 9.5)]
    [InlineData("奥克兰", 13, 12)]
    public void 有夏令时的城市冬夏偏移不同(string city, double winterHours, double summerHours)
    {
        var entry = WorldClockCatalog.All.Single(c => c.City == city);

        Assert.Equal(TimeSpan.FromHours(winterHours), WorldClockTime.OffsetAt(Winter, entry));
        Assert.Equal(TimeSpan.FromHours(summerHours), WorldClockTime.OffsetAt(Summer, entry));
    }

    [Theory]
    // 这些城市后来取消/恢复过夏令时，靠系统 tzdata 会算错，内置规则必须稳定
    [InlineData("莫斯科", 3)]
    [InlineData("德黑兰", 3.5)]
    [InlineData("圣保罗", -3)]
    [InlineData("北京", 8)]
    [InlineData("东京", 9)]
    [InlineData("努库阿洛法", 13)]
    [InlineData("圣诞岛", 14)]
    [InlineData("帕果帕果", -11)]
    public void 无夏令时的城市全年固定(string city, double hours)
    {
        var entry = WorldClockCatalog.All.Single(c => c.City == city);

        Assert.Equal(TimeSpan.FromHours(hours), WorldClockTime.OffsetAt(Winter, entry));
        Assert.Equal(TimeSpan.FromHours(hours), WorldClockTime.OffsetAt(Summer, entry));
    }

    [Fact]
    public void 纽芬兰半点时区也跟随北美夏令时()
    {
        var entry = WorldClockCatalog.All.Single(c => c.City == "圣约翰斯");

        Assert.Equal(TimeSpan.FromMinutes(-210), WorldClockTime.OffsetAt(Winter, entry));
        Assert.Equal(TimeSpan.FromMinutes(-150), WorldClockTime.OffsetAt(Summer, entry));
    }

    [Fact]
    public void 夏令时切换当天按小时生效()
    {
        var london = WorldClockCatalog.All.Single(c => c.City == "伦敦");

        // 2026-03-29 01:00 UTC 起伦敦进入 BST
        Assert.Equal(TimeSpan.Zero, WorldClockTime.OffsetAt(new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc), london));
        Assert.Equal(TimeSpan.FromHours(1), WorldClockTime.OffsetAt(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), london));

        // 2026-10-25 01:00 UTC 回到 GMT
        Assert.Equal(TimeSpan.FromHours(1), WorldClockTime.OffsetAt(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), london));
        Assert.Equal(TimeSpan.Zero, WorldClockTime.OffsetAt(new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc), london));
    }

    [Fact]
    public void 南半球夏令时跨年也算得对()
    {
        var sydney = WorldClockCatalog.All.Single(c => c.City == "悉尼");

        // 12 月（本年 10 月开始、次年 4 月结束）
        Assert.True(WorldClockTime.IsDaylightSaving(new DateTime(2026, 12, 20, 3, 0, 0, DateTimeKind.Utc), sydney));
        // 2 月（去年 10 月开始、本年 4 月结束）
        Assert.True(WorldClockTime.IsDaylightSaving(new DateTime(2026, 2, 20, 3, 0, 0, DateTimeKind.Utc), sydney));
        // 6 月（冬令时）
        Assert.False(WorldClockTime.IsDaylightSaving(new DateTime(2026, 6, 20, 3, 0, 0, DateTimeKind.Utc), sydney));
    }

    // ---------------- 本地时间 ----------------

    [Fact]
    public void 本地时间按偏移换算()
    {
        var beijing = WorldClockCatalog.All.Single(c => c.City == "北京");
        var newYork = WorldClockCatalog.All.Single(c => c.City == "纽约");

        var utc = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 1, 15, 20, 0, 0), WorldClockTime.LocalTimeAt(utc, beijing));
        Assert.Equal(new DateTime(2026, 1, 15, 7, 0, 0), WorldClockTime.LocalTimeAt(utc, newYork));
    }

    [Fact]
    public void 跨日界线时日期会差一天()
    {
        var utc = new DateTime(2026, 1, 15, 20, 0, 0, DateTimeKind.Utc);

        var pagoPago = WorldClockCatalog.All.Single(c => c.City == "帕果帕果");
        var kiritimati = WorldClockCatalog.All.Single(c => c.City == "圣诞岛");

        // UTC-11 → 1 月 15 日 09:00；UTC+14 → 1 月 16 日 10:00
        Assert.Equal(new DateTime(2026, 1, 15, 9, 0, 0), WorldClockTime.LocalTimeAt(utc, pagoPago));
        Assert.Equal(new DateTime(2026, 1, 16, 10, 0, 0), WorldClockTime.LocalTimeAt(utc, kiritimati));
    }
}
