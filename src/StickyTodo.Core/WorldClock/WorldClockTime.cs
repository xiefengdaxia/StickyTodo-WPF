using System.Globalization;

namespace StickyTodo.Core.WorldClock;

/// <summary>
/// 时区换算：给定 UTC 时刻，算出某个城市此刻的偏移与本地时间。
///
/// 全部基于内置的 <see cref="DaylightRule"/>，不查系统时区数据库，
/// 所以 Win7（tzdata 停在 2011 年）和 Win11 上算出来的钟点一致。
/// 夏令时起止按"通用规则"（第 N 个周日）推算，个别年份的临时调整可能差几天，
/// 对"现在几点"这种展示完全够用。
/// </summary>
public static class WorldClockTime
{
    /// <summary>此刻该城市的偏移（含夏令时）。</summary>
    public static TimeSpan OffsetAt(DateTime utcNow, WorldClockCity city)
    {
        if (city.Rule == DaylightRule.None || !IsDaylightSaving(utcNow, city))
        {
            return city.StandardOffset;
        }

        return city.StandardOffset + TimeSpan.FromHours(1);
    }

    /// <summary>此刻该城市的本地时间。</summary>
    public static DateTime LocalTimeAt(DateTime utcNow, WorldClockCity city) =>
        DateTime.SpecifyKind(utcNow + OffsetAt(utcNow, city), DateTimeKind.Unspecified);

    /// <summary>此刻该城市是否处于夏令时。</summary>
    public static bool IsDaylightSaving(DateTime utcNow, WorldClockCity city)
    {
        if (city.Rule == DaylightRule.None)
        {
            return false;
        }

        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var year = utc.Year;

        switch (city.Rule)
        {
            case DaylightRule.UnitedStates:
            {
                // 本地 02:00 标准时切换
                var start = ToUtc(NthSunday(year, 3, 2).AddHours(2), city.StandardOffset);
                var end = ToUtc(NthSunday(year, 11, 1).AddHours(2), city.StandardOffset);
                return utc >= start && utc < end;
            }

            case DaylightRule.Europe:
            {
                // 01:00 UTC 切换
                var start = LastSunday(year, 3).AddHours(1);
                var end = LastSunday(year, 10).AddHours(1);
                return utc >= start && utc < end;
            }

            case DaylightRule.Egypt:
            {
                // 4 月最后一个周五开始，10 月最后一个周五结束（本地 00:00）
                var start = ToUtc(LastFriday(year, 4), city.StandardOffset);
                var end = ToUtc(LastFriday(year, 10), city.StandardOffset);
                return utc >= start && utc < end;
            }

            case DaylightRule.Chile:
                return InSouthernWindow(
                    utc,
                    y => ToUtc(FirstSunday(y, 9), city.StandardOffset),
                    y => ToUtc(FirstSunday(y, 4), city.StandardOffset));

            case DaylightRule.AustraliaSouth:
                return InSouthernWindow(
                    utc,
                    y => ToUtc(FirstSunday(y, 10).AddHours(2), city.StandardOffset),
                    y => ToUtc(FirstSunday(y, 4).AddHours(2), city.StandardOffset));

            case DaylightRule.NewZealand:
                return InSouthernWindow(
                    utc,
                    y => ToUtc(LastSunday(y, 9).AddHours(2), city.StandardOffset),
                    y => ToUtc(FirstSunday(y, 4).AddHours(2), city.StandardOffset));

            default:
                return false;
        }
    }

    /// <summary>偏移文本：<c>+8</c> / <c>-5</c> / <c>+5:30</c> / <c>+0</c>。</summary>
    public static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        var hours = (int)absolute.TotalHours;
        var minutes = absolute.Minutes;

        return minutes == 0
            ? sign + hours.ToString(CultureInfo.InvariantCulture)
            : sign + hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>带 UTC 前缀的偏移标签：<c>UTC+8</c>。</summary>
    public static string FormatOffsetLabel(TimeSpan offset) => "UTC" + FormatOffset(offset);

    // ---------------- 内部：本地时间 → UTC，以及"第 N 个周日" ----------------

    private static DateTime ToUtc(DateTime localStandard, TimeSpan standardOffset) =>
        DateTime.SpecifyKind(localStandard - standardOffset, DateTimeKind.Utc);

    /// <summary>南半球夏令时跨年：[今年 10 月, 次年 4 月)。</summary>
    private static bool InSouthernWindow(DateTime utc, Func<int, DateTime> startOf, Func<int, DateTime> endOf)
    {
        var year = utc.Year;
        return (utc >= startOf(year) && utc < endOf(year + 1))
               || (utc >= startOf(year - 1) && utc < endOf(year));
    }

    private static DateTime FirstSunday(int year, int month) => NthSunday(year, month, 1);

    private static DateTime NthSunday(int year, int month, int n)
    {
        var first = new DateTime(year, month, 1);
        var offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + ((n - 1) * 7));
    }

    private static DateTime LastSunday(int year, int month)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(int)last.DayOfWeek);
    }

    private static DateTime LastFriday(int year, int month)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var back = ((int)last.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7;
        return last.AddDays(-back);
    }
}
