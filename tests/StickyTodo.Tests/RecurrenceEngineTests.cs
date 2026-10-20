using StickyTodo.Core.Calendar;
using StickyTodo.Core.Recurrence;

namespace StickyTodo.Tests;

public sealed class RecurrenceEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Daily_ProducesEveryDay()
    {
        var dates = RecurrenceEngine.Occurrences(Start, "FREQ=DAILY", Start, Start.AddDays(4));

        Assert.Equal(5, dates.Count);
        Assert.Equal(Start.Date, dates[0].Date);
        Assert.Equal(Start.AddDays(4).Date, dates[^1].Date);
    }

    [Fact]
    public void WeeklyByDay_OnlyMatchesGivenWeekdays()
    {
        // 2026-10-20 是周二
        var dates = RecurrenceEngine.Occurrences(
            Start, "FREQ=WEEKLY;BYDAY=MO,WE", Start, Start.AddDays(14));

        Assert.All(dates, d => Assert.True(
            d.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday,
            $"{d:yyyy-MM-dd} 是 {d.DayOfWeek}"));
        Assert.Equal(4, dates.Count);
    }

     [Fact]
    public void Occurrences_ExcludeStart_WhenItDoesNotMatchByDay()
    {
        // 起始日 2026-10-20 是周二，但规则要求周一/周三 → 周二不应被算作一次发生
        var start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(8));
        var dates = RecurrenceEngine.Occurrences(start, "FREQ=WEEKLY;BYDAY=MO,WE", start, start.AddDays(14));

        Assert.All(dates, d => Assert.True(d.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday));
        Assert.Equal(new DateTime(2026, 10, 21, 9, 0, 0), dates[0].DateTime);
    }

    [Fact]
    public void Occurrences_KeepStart_WhenItMatchesRule()
    {
        var start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(8)); // 周二
        var dates = RecurrenceEngine.Occurrences(start, "FREQ=WEEKLY;BYDAY=TU", start, start.AddDays(7));

        Assert.Equal(2, dates.Count);
        Assert.Equal(DayOfWeek.Tuesday, dates[0].DayOfWeek);
        Assert.Equal(start.DateTime, dates[0].DateTime);
    }

    [Fact]
    public void Weekdays_ExcludesWeekend()
    {
        var dates = RecurrenceEngine.Occurrences(
            Start, "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", Start, Start.AddDays(13));

        // 起始日 10-20(周二) 到 11-02 之间的工作日共 10 个（含首尾当天，跳过两个周末）
        Assert.Equal(10, dates.Count);
        Assert.DoesNotContain(dates, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    [Fact]
    public void MonthlyLastDay_AlwaysLastDay()
    {
        var dates = RecurrenceEngine.Occurrences(
            new DateTimeOffset(2026, 1, 31, 9, 0, 0, TimeSpan.FromHours(8)),
            "FREQ=MONTHLY;BYMONTHDAY=-1",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 6, 30, 23, 0, 0, TimeSpan.FromHours(8)));

        Assert.Equal(6, dates.Count);
        foreach (var d in dates)
        {
            Assert.Equal(d.Day, DateTime.DaysInMonth(d.Year, d.Month));
        }
    }

    [Fact]
    public void SecondTuesdayOfMonth()
    {
        var dates = RecurrenceEngine.Occurrences(
            new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(8)),
            "FREQ=MONTHLY;BYDAY=2TU",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 4, 30, 23, 0, 0, TimeSpan.FromHours(8)));

        Assert.Equal(4, dates.Count);
        Assert.All(dates, d => Assert.Equal(DayOfWeek.Tuesday, d.DayOfWeek));
        Assert.All(dates, d => Assert.InRange(d.Day, 8, 14));
    }

    [Fact]
    public void Count_LimitsOccurrences()
    {
        var dates = RecurrenceEngine.Occurrences(Start, "FREQ=DAILY;COUNT=3", Start, Start.AddDays(30));
        Assert.Equal(3, dates.Count);
    }

    [Fact]
    public void Until_StopsAtDate()
    {
        var until = Start.AddDays(2).ToString("yyyyMMdd");
        var dates = RecurrenceEngine.Occurrences(Start, $"FREQ=DAILY;UNTIL={until}T235959", Start, Start.AddDays(30));
        Assert.Equal(3, dates.Count);
    }

    [Fact]
    public void Next_ReturnsFirstAfterGivenMoment()
    {
        var next = RecurrenceEngine.Next(Start, "FREQ=DAILY", Start.AddHours(1));
        Assert.Equal(Start.AddDays(1).Date, next!.Value.Date);
    }

    [Fact]
    public void Next_ReturnsNull_WhenRuleHasCount()
    {
        var next = RecurrenceEngine.Next(Start, "FREQ=DAILY;COUNT=1", Start.AddDays(1));
        Assert.Null(next);
    }

    [Theory]
    [InlineData("FREQ=DAILY", true)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("FREQ=NOPE", false)]
    [InlineData("不是规则", false)]
    public void IsValid_RecognizesGarbage(string? rule, bool expected)
    {
        Assert.Equal(expected, RecurrenceEngine.IsValid(rule));
    }

    [Theory]
    [InlineData("FREQ=DAILY", "每天")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2", "每 2 周")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", "每个工作日")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE", "每周一、三")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1", "每月最后一天")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", "每月最后个五")]
    [InlineData("FREQ=MONTHLY;BYDAY=2TU", "每月第 2 个二")]
    [InlineData("FREQ=YEARLY", "每年")]
    public void Describe_ProducesReadableChinese(string rule, string expected)
    {
        Assert.Equal(expected, RecurrenceEngine.Describe(rule));
    }

    [Fact]
    public void Describe_AppendsCountAndUntil()
    {
        Assert.Contains("共 5 次", RecurrenceEngine.Describe("FREQ=DAILY;COUNT=5"));
        Assert.Contains("直到 2027-01-01", RecurrenceEngine.Describe("FREQ=DAILY;UNTIL=20270101T000000"));
    }

    [Fact]
    public void Presets_AreAllValid()
    {
        foreach (var (name, rule) in RecurrenceEngine.Presets)
        {
            Assert.True(RecurrenceEngine.IsValid(rule), $"预设「{name}」无效：{rule}");
        }
    }
}

public sealed class MonthGridTests
{
    [Fact]
    public void Build_AlwaysProducesSixWeeks()
    {
        var cells = MonthGrid.Build(2026, 10);
        Assert.Equal(42, cells.Count);
    }

    [Fact]
    public void Build_MarksOnlyCurrentMonth_AsInMonth()
    {
        var cells = MonthGrid.Build(2026, 10);
        Assert.Equal(31, cells.Count(c => c.IsCurrentMonth));
        Assert.All(cells.Where(c => c.IsCurrentMonth), c => Assert.Equal(10, c.Date.Month));
    }

    [Fact]
    public void Build_StartsOnMonday_WhenRequested()
    {
        var cells = MonthGrid.Build(2026, 10, DayOfWeek.Monday);
        Assert.Equal(DayOfWeek.Monday, cells[0].Date.DayOfWeek);
    }

    [Fact]
    public void Build_StartsOnSunday_WhenRequested()
    {
        var cells = MonthGrid.Build(2026, 10, DayOfWeek.Sunday);
        Assert.Equal(DayOfWeek.Sunday, cells[0].Date.DayOfWeek);
    }

    [Fact]
    public void Build_LeapFebruary_Has29InMonthDays()
    {
        var cells = MonthGrid.Build(2028, 2);
        Assert.Equal(29, cells.Count(c => c.IsCurrentMonth));
    }

    [Theory]
    [InlineData(2026, 12, 1, 2027, 1)]
    [InlineData(2026, 1, -1, 2025, 12)]
    [InlineData(2026, 6, 0, 2026, 6)]
    [InlineData(2026, 3, 12, 2027, 3)]
    public void Shift_HandlesYearBoundary(int year, int month, int delta, int expectedYear, int expectedMonth)
    {
        var (y, m) = MonthGrid.Shift(year, month, delta);
        Assert.Equal(expectedYear, y);
        Assert.Equal(expectedMonth, m);
    }

    [Fact]
    public void Build_ConsecutiveDays_WithoutGaps()
    {
        var cells = MonthGrid.Build(2026, 10);
        for (var i = 1; i < cells.Count; i++)
        {
            Assert.Equal(cells[i - 1].Date.AddDays(1), cells[i].Date);
        }
    }
}