using StickyTodo.Core.Calendar;
using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

/// <summary>
/// 导入的节假日/调休（MCP 的 import_holidays 写设置键 calendar.holidays）：
/// JSON 往返、以及"导入优先于内置数据"这条优先级。
///
/// <see cref="ChineseHolidays"/> 是进程级静态表，用例跑完必须还原，
/// 所以和 LunarCalendarTests 放进同一个 collection（xUnit 里同 collection 不并行）。
/// </summary>
[Collection("ChineseHolidays")]
public sealed class HolidayImportTests : DatabaseTestBase
{
    [Fact]
    public void ImportedSpans_RoundTripThroughSettings_AndWin()
    {
        try
        {
            var settings = new AppSettingsService(Repository);
            settings.HolidayImports =
            [
                new ImportedHoliday("公司假", new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 4), DayKind.Holiday),
                new ImportedHoliday("补班", new DateOnly(2026, 3, 7), new DateOnly(2026, 3, 7), DayKind.MakeupWorkday),
            ];

            // 存的是 JSON：新建一个服务读回来必须一模一样
            var reloaded = new AppSettingsService(Repository);
            var parsed = reloaded.HolidayImports;
            Assert.Equal(2, parsed.Count);
            Assert.Equal("公司假", parsed[0].Name);
            Assert.Equal(new DateOnly(2026, 3, 2), parsed[0].From);
            Assert.Equal(new DateOnly(2026, 3, 4), parsed[0].To);
            Assert.Equal(DayKind.MakeupWorkday, parsed[1].Kind);

            // 查询优先用导入的数据：3/2 变成假期、3/7（周六）变成补班
            Assert.Equal(DayKind.Holiday, ChineseHolidays.KindOf(new DateOnly(2026, 3, 2)));
            Assert.Equal("公司假", ChineseHolidays.HolidayNameOf(new DateOnly(2026, 3, 2)));
            Assert.Equal("休", ChineseHolidays.BadgeOf(new DateOnly(2026, 3, 2)));
            Assert.Equal(DayKind.MakeupWorkday, ChineseHolidays.KindOf(new DateOnly(2026, 3, 7)));
            Assert.Equal("班", ChineseHolidays.BadgeOf(new DateOnly(2026, 3, 7)));

            // get_holidays 用的条目列表里标了"来自导入"
            var entries = ChineseHolidays.EntriesOf(2026);
            Assert.Contains(entries, e => e.Date == new DateOnly(2026, 3, 2) && e.Imported && e.Name == "公司假");
            Assert.Contains(entries, e => e.Date == new DateOnly(2026, 10, 1) && !e.Imported);
        }
        finally
        {
            ChineseHolidays.SetImported(null);
        }
    }

    [Fact]
    public void ImportedWorkday_CancelsBuiltInHoliday()
    {
        try
        {
            // 2026-10-01 内置是国庆假期；导入成工作日之后那天就按工作日算
            var settings = new AppSettingsService(Repository);
            settings.HolidayImports =
            [
                new ImportedHoliday("值班", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), DayKind.Workday),
            ];

            var day = new DateOnly(2026, 10, 1);
            Assert.Equal(DayKind.Workday, ChineseHolidays.KindOf(day));
            Assert.Null(ChineseHolidays.HolidayNameOf(day));
            Assert.Equal(string.Empty, ChineseHolidays.BadgeOf(day));

            // 同一天在条目列表里只剩导入的那一条
            var entries = ChineseHolidays.EntriesOf(2026).Where(e => e.Date == day).ToList();
            Assert.Single(entries);
            Assert.True(entries[0].Imported);
            Assert.Equal(DayKind.Workday, entries[0].Kind);
        }
        finally
        {
            ChineseHolidays.SetImported(null);
        }
    }

    [Fact]
    public void BrokenJson_IsTreatedAsNoImport()
    {
        Assert.Empty(HolidayImportStore.Parse("{ 这不是 JSON"));
        Assert.Empty(HolidayImportStore.Parse(string.Empty));
        Assert.Equal(string.Empty, HolidayImportStore.Serialize([]));
    }
}
