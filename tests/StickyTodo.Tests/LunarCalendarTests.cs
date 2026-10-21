using StickyTodo.Core.Calendar;

namespace StickyTodo.Tests;

/// <summary>
/// 农历/节气/黄历的算法测试。
/// 期望值取自公开的历法事实（春节日期、闰月、节气时刻），不依赖任何第三方库。
/// </summary>
public sealed class LunarCalendarTests
{
    [Theory]
    // 每年春节：农历正月初一 ↔ 公历
    [InlineData(2024, 2, 10)]
    [InlineData(2025, 1, 29)]
    [InlineData(2026, 2, 17)]
    [InlineData(2027, 2, 6)]
    [InlineData(2023, 1, 22)]
    public void SpringFestival_IsLunarNewYearDay(int year, int month, int day)
    {
        var lunar = LunarCalendar.SolarToLunar(new DateOnly(year, month, day));

        Assert.True(lunar.IsValid);
        Assert.Equal(1, lunar.Month);
        Assert.Equal(1, lunar.Day);
        Assert.False(lunar.IsLeapMonth);
        Assert.Equal(year, lunar.Year);
    }

    [Theory]
    [InlineData(2020, 5, 23)] // 闰四月初一
    [InlineData(2023, 3, 22)] // 闰二月初一
    [InlineData(2025, 7, 25)] // 闰六月初一
    public void LeapMonth_IsDetected(int year, int month, int day)
    {
        var lunar = LunarCalendar.SolarToLunar(new DateOnly(year, month, day));

        Assert.True(lunar.IsLeapMonth);
        Assert.Equal(1, lunar.Day);
    }

    [Fact]
    public void LeapMonth_Four_IsReported()
    {
        Assert.Equal(4, LunarCalendar.LeapMonth(2020));
        Assert.Equal(2, LunarCalendar.LeapMonth(2023));
        Assert.Equal(6, LunarCalendar.LeapMonth(2025));
    }

    [Fact]
    public void LeapMonth_None_IsZero()
    {
        Assert.Equal(0, LunarCalendar.LeapMonth(2024));
        Assert.Equal(0, LunarCalendar.LeapMonth(2026));
    }

    [Theory]
    [InlineData(2026, 2, 17, "正月初一")]
    [InlineData(2026, 10, 21, "九月十二")]
    [InlineData(2024, 2, 10, "正月初一")]
    [InlineData(2025, 10, 6, "八月十五")] // 2025 中秋
    public void LunarText_MatchesWellKnownDates(int year, int month, int day, string expected)
    {
        var lunar = LunarCalendar.SolarToLunar(new DateOnly(year, month, day));

        Assert.Equal(expected, lunar.FullText);
    }

    [Fact]
    public void RoundTrip_IsStableAcrossAYear()
    {
        var start = new DateOnly(2024, 1, 1);
        for (var i = 0; i < 800; i++)
        {
            var solar = start.AddDays(i);
            var lunar = LunarCalendar.SolarToLunar(solar);

            Assert.True(lunar.IsValid, $"{solar} 应能转成农历");
            Assert.Equal(solar, LunarCalendar.LunarToSolar(lunar));
        }
    }

    [Fact]
    public void MonthDays_Are29Or30()
    {
        for (var year = 2024; year <= 2026; year++)
        {
            for (var month = 1; month <= 12; month++)
            {
                var days = LunarCalendar.MonthDays(year, month);
                Assert.InRange(days, 29, 30);
            }
        }
    }

    [Theory]
    [InlineData(2024, "甲辰", "龙")]
    [InlineData(2025, "乙巳", "蛇")]
    [InlineData(2026, "丙午", "马")]
    public void YearGanZhi_MatchesKnownYears(int year, string ganzhi, string zodiac)
    {
        Assert.Equal(ganzhi, LunarCalendar.YearGanZhi(year));
        Assert.Equal(zodiac, LunarCalendar.YearZodiac(year));
    }

    [Fact]
    public void OutOfRange_ReturnsInvalid()
    {
        Assert.False(LunarCalendar.SolarToLunar(new DateOnly(1899, 12, 31)).IsValid);
        Assert.False(LunarCalendar.SolarToLunar(new DateOnly(2101, 1, 1)).IsValid);
    }
}

/// <summary>节气测试：用公开的节气日期核对。</summary>
public sealed class SolarTermTests
{
    [Theory]
    [InlineData(2024, "立春", 2, 4)]
    [InlineData(2024, "清明", 4, 4)]
    [InlineData(2024, "夏至", 6, 21)]
    [InlineData(2024, "秋分", 9, 22)]
    [InlineData(2024, "冬至", 12, 21)]
    [InlineData(2025, "立春", 2, 3)]
    [InlineData(2025, "清明", 4, 4)]
    [InlineData(2025, "冬至", 12, 21)]
    [InlineData(2026, "立春", 2, 4)]
    [InlineData(2026, "清明", 4, 5)]
    public void KnownTerms_FallOnExpectedDates(int year, string name, int month, int day)
    {
        var expected = new DateOnly(year, month, day);
        var actual = SolarTerms.DateOf(year, Array.IndexOf(SolarTerms.Names, name));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AllTerms_AreInChronologicalOrder()
    {
        for (var year = 2024; year <= 2026; year++)
        {
            var terms = SolarTerms.Of(year);
            Assert.Equal(24, terms.Count);

            for (var i = 1; i < terms.Count; i++)
            {
                Assert.True(terms[i].Date > terms[i - 1].Date,
                    $"{year} 年 {terms[i].Name} 应在 {terms[i - 1].Name} 之后");
            }
        }
    }

    [Fact]
    public void NameOn_ReturnsTermName()
    {
        Assert.Equal("清明", SolarTerms.NameOn(new DateOnly(2026, 4, 5)));
        Assert.Equal("冬至", SolarTerms.NameOn(new DateOnly(2024, 12, 21)));
        Assert.Null(SolarTerms.NameOn(new DateOnly(2026, 4, 6)));
    }

    [Theory]
    [InlineData(2026, 2, 4, 2)]  // 立春后进寅月
    [InlineData(2026, 3, 5, 3)]  // 惊蛰后进卯月
    [InlineData(2026, 4, 5, 4)]  // 清明后进辰月
    [InlineData(2026, 12, 7, 0)] // 大雪后进子月
    public void MonthBranch_FollowsSolarTerms(int year, int month, int day, int expected)
    {
        Assert.Equal(expected, SolarTerms.MonthBranchIndex(new DateOnly(year, month, day)));
    }
}

/// <summary>节假日与调休测试。</summary>
public sealed class ChineseHolidayTests
{
    [Theory]
    [InlineData(2026, 2, 17, "春节")]
    [InlineData(2026, 10, 1, "国庆")]
    [InlineData(2026, 1, 2, "元旦")]
    [InlineData(2025, 10, 6, "国庆·中秋")]
    [InlineData(2024, 2, 12, "春节")]
    public void Holidays_AreRecognized(int year, int month, int day, string name)
    {
        var date = new DateOnly(year, month, day);

        Assert.Equal(DayKind.Holiday, ChineseHolidays.KindOf(date));
        Assert.Equal(name, ChineseHolidays.HolidayNameOf(date));
        Assert.Equal("休", ChineseHolidays.BadgeOf(date));
    }

    [Theory]
    [InlineData(2026, 1, 4)]  // 元旦补班
    [InlineData(2026, 2, 14)] // 春节补班
    [InlineData(2026, 2, 28)]
    [InlineData(2026, 5, 9)]
    [InlineData(2026, 9, 20)]
    [InlineData(2026, 10, 10)]
    public void MakeupDays_AreWorkdays(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);

        Assert.True(date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            "补班日按惯例都落在周末");
        Assert.Equal(DayKind.MakeupWorkday, ChineseHolidays.KindOf(date));
        Assert.Equal("班", ChineseHolidays.BadgeOf(date));
    }

    [Theory]
    [InlineData(2026, 10, 21)] // 普通周三
    [InlineData(2026, 3, 3)]
    public void OrdinaryWeekdays_AreWorkdays(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);

        Assert.Equal(DayKind.Workday, ChineseHolidays.KindOf(date));
        Assert.Equal(string.Empty, ChineseHolidays.BadgeOf(date));
        Assert.Null(ChineseHolidays.HolidayNameOf(date));
    }

    [Fact]
    public void OrdinaryWeekend_IsWeekend()
    {
        var date = new DateOnly(2026, 10, 24); // 周六，不调休
        Assert.Equal(DayKind.Weekend, ChineseHolidays.KindOf(date));
        Assert.Equal(string.Empty, ChineseHolidays.BadgeOf(date));
    }

    [Fact]
    public void Coverage_MatchesDeclaredYears()
    {
        Assert.True(ChineseHolidays.HasYear(2026));
        Assert.False(ChineseHolidays.HasYear(2027));
        Assert.Contains("2024", ChineseHolidays.CoverageText);
    }

    [Fact]
    public void EveryCoveredYear_HasSpringFestival()
    {
        for (var year = ChineseHolidays.MinYear; year <= ChineseHolidays.MaxYear; year++)
        {
            var spans = Enumerable.Range(0, 366)
                .Select(i => new DateOnly(year, 1, 1).AddDays(i))
                .Where(d => d.Year == year)
                .Select(ChineseHolidays.HolidayNameOf)
                .Where(n => n is not null)
                .ToHashSet();

            Assert.Contains(spans, n => n!.Contains("春节"));
        }
    }
}

/// <summary>黄历测试。</summary>
public sealed class AlmanacTests
{
    [Fact]
    public void Describe_ReturnsAllParts()
    {
        var day = Almanac.Describe(new DateOnly(2026, 10, 21));

        Assert.Equal(new DateOnly(2026, 10, 21), day.Date);
        Assert.True(day.Lunar.IsValid);
        Assert.Equal("九月十二", day.Lunar.FullText);
        Assert.False(string.IsNullOrEmpty(day.GanZhi));
        Assert.Contains(day.Officer, Almanac.Officers);
        Assert.NotEmpty(day.Yi);
        Assert.NotEmpty(day.Ji);
    }

    [Fact]
    public void Officer_CyclesEveryTwelveDays()
    {
        // 建除十二神按 12 天一轮，但跨「节」换月时基准会跳变，
        // 所以取一段确定落在同一节气月内（芒种之后、小暑之前）的日期
        var start = new DateOnly(2026, 6, 10);
        for (var i = 0; i < 10; i++)
        {
            var a = Almanac.OfficerOf(start.AddDays(i)).Index;
            var b = Almanac.OfficerOf(start.AddDays(i + 12)).Index;
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void OfficerOf_MatchesDayGanZhiBranch()
    {
        var date = new DateOnly(2026, 10, 21);
        var (index, name) = Almanac.OfficerOf(date);

        Assert.Equal(Almanac.Officers[index], name);
        Assert.InRange(index, 0, 11);
    }

    [Fact]
    public void YiAndJi_AreNeverEmpty()
    {
        var date = new DateOnly(2026, 1, 1);
        for (var i = 0; i < 365; i++)
        {
            var day = date.AddDays(i);
            Assert.NotEmpty(Almanac.YiOf(day));
            Assert.NotEmpty(Almanac.JiOf(day));
        }
    }

    [Fact]
    public void DayGanZhi_IsSixtyCycle()
    {
        var start = new DateOnly(2026, 1, 1);
        Assert.Equal(LunarCalendar.DayGanZhi(start), LunarCalendar.DayGanZhi(start.AddDays(60)));

        var seen = new HashSet<string>();
        for (var i = 0; i < 60; i++)
        {
            seen.Add(LunarCalendar.DayGanZhi(start.AddDays(i)));
        }

        Assert.Equal(60, seen.Count);
    }
}