using System.Text;
using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.DataTypes;
using IcalCalendar = Ical.Net.Calendar;

namespace StickyTodo.Core.Recurrence;

/// <summary>
/// 重复规则引擎：直接用 RFC 5545 的 RRULE 语法（iCalendar 标准），
/// 不自己发明一套规则，因此导出给 Google/Outlook 也认。
/// 这里只做三件事：校验、取某区间内的发生时间、生成中文描述。
/// </summary>
public static class RecurrenceEngine
{
    /// <summary>常用预设。Key 存进 todo.recurrence。</summary>
    public static readonly IReadOnlyList<(string Name, string Rule)> Presets =
    [
        ("每天", "FREQ=DAILY"),
        ("每个工作日", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"),
        ("每周", "FREQ=WEEKLY"),
        ("每两周", "FREQ=WEEKLY;INTERVAL=2"),
        ("每月", "FREQ=MONTHLY"),
        ("每月最后一天", "FREQ=MONTHLY;BYMONTHDAY=-1"),
        ("每年", "FREQ=YEARLY"),
    ];

    private static readonly Regex RulePart = new(@"([A-Z]+)=([^;]+)", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownFrequencies = new(StringComparer.Ordinal)
    {
        "DAILY", "WEEKLY", "MONTHLY", "YEARLY",
    };

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "FREQ", "INTERVAL", "COUNT", "UNTIL", "BYDAY", "BYMONTHDAY", "BYMONTH", "BYYEARDAY", "WKST",
    };

    /// <summary>
    /// 规则格式是否合法。注意：只靠 iCal 库「能算出结果」判断是不够的——
    /// 塞进去一条 "FREQ=NOPE" 会解析失败并被静默忽略，DTSTART 仍然返回 1 条，
    /// 于是看起来「有效」。所以必须先做一次严格的格式校验。
    /// </summary>
    private static bool IsWellFormed(string rrule)
    {
        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            var match = RulePart.Match(part);
            if (!match.Success)
            {
                return false;
            }

            var key = match.Groups[1].Value;
            if (!KnownKeys.Contains(key) || map.ContainsKey(key))
            {
                return false;
            }

            map[key] = match.Groups[2].Value;
        }

        if (!map.TryGetValue("FREQ", out var freq) || !KnownFrequencies.Contains(freq))
        {
            return false;
        }

        if (map.TryGetValue("INTERVAL", out var interval) &&
            (!int.TryParse(interval, out var n) || n < 1))
        {
            return false;
        }

        if (map.TryGetValue("COUNT", out var count) &&
            (!int.TryParse(count, out var c) || c < 1))
        {
            return false;
        }

        return true;
    }

    /// <summary>规则是否可用（格式合法，且能算出发生时间）。</summary>
    public static bool IsValid(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule) || !IsWellFormed(rrule.Trim().ToUpperInvariant()))
        {
            return false;
        }

        try
        {
            // 用「从今天起一年内有没有发生时间」来判断规则是否讲得通
            var occurrences = Occurrences(DateTimeOffset.Now, rrule, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
            return occurrences.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><paramref name="after"/>（不含）之后的第一个发生时间；没有则 null。</summary>
    public static DateTimeOffset? Next(DateTimeOffset start, string rrule, DateTimeOffset after)
    {
        // 往后找 5 年足够覆盖「每年」这类低频规则
        var occurrences = Occurrences(start, rrule, after.AddMinutes(1), after.AddYears(5));
        return occurrences.Count == 0 ? null : occurrences[0];
    }

    /// <summary>
    /// [from, to] 区间内的所有发生时间（两端都是闭区间）。
    /// iCal 的检索上界是开区间，所以内部把 to 多推一分钟，保证「落在 to 当天最后一次」不会漏。
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Occurrences(
        DateTimeOffset start,
        string rrule,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        if (string.IsNullOrWhiteSpace(rrule) || to < from)
        {
            return [];
        }

        var calendar = IcalCalendar.Load(BuildIcs(start, rrule));
        var occurrences = calendar.GetOccurrences(
            new CalDateTime(ToFloating(from)),
            new CalDateTime(ToFloating(to.AddMinutes(1))));

        var result = occurrences
            .Select(o => ToLocalDateTimeOffset(o.Period.StartTime.Value))
            .OrderBy(d => d)
            .ToList();

        // iCal.Net 会把 DTSTART 无条件算作一次发生，即使它不满足 BYDAY/BYMONTHDAY。
        // 例如「每周一、三」而起始日是周二时，周二会被错误地包含进来。
        // 这里把不满足约束的 DTSTART 剔除掉。
        if (result.Count > 0
            && Math.Abs((result[0] - start).TotalMinutes) < 1
            && !StartSatisfiesConstraints(start, rrule))
        {
            result.RemoveAt(0);
        }

        return result;
    }

    /// <summary>中文描述，界面上直接显示。</summary>
    public static string Describe(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return string.Empty;
        }

        var parts = RulePart.Matches(rrule.ToUpperInvariant())
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        var interval = parts.TryGetValue("INTERVAL", out var raw) && int.TryParse(raw, out var n) && n > 1 ? n : 1;
        var freq = parts.TryGetValue("FREQ", out var f) ? f : string.Empty;

        var text = freq switch
        {
            "DAILY" => interval == 1 ? "每天" : $"每 {interval} 天",
            "WEEKLY" => DescribeWeekly(parts, interval),
            "MONTHLY" => DescribeMonthly(parts, interval),
            "YEARLY" => interval == 1 ? "每年" : $"每 {interval} 年",
            _ => rrule,
        };

        if (parts.TryGetValue("UNTIL", out var until) && until.Length >= 8)
        {
            text += $"，直到 {until[..4]}-{until.Substring(4, 2)}-{until.Substring(6, 2)}";
        }
        else if (parts.TryGetValue("COUNT", out var count))
        {
            text += $"，共 {count} 次";
        }

        return text;
    }

    /// <summary>
    /// DTSTART 本身是否满足规则的约束（用于剔除 iCal.Net 硬塞进来的那个起始日期）。
    /// 只处理最常用的 BYDAY / BYMONTHDAY 两类，其余规则一律认为满足。
    /// </summary>
    private static bool StartSatisfiesConstraints(DateTimeOffset start, string rrule)
    {
        var parts = RulePart.Matches(rrule.ToUpperInvariant())
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        if (parts.TryGetValue("BYMONTHDAY", out var monthDayText))
        {
            var days = monthDayText.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var daysInMonth = DateTime.DaysInMonth(start.Year, start.Month);
            var matched = days.Any(d =>
                int.TryParse(d, out var n) &&
                (n > 0 ? n == start.Day : daysInMonth + n + 1 == start.Day));

            if (!matched)
            {
                return false;
            }
        }

        if (!parts.TryGetValue("BYDAY", out var byDayText))
        {
            return true;
        }

        var startCode = start.DayOfWeek switch
        {
            DayOfWeek.Monday => "MO",
            DayOfWeek.Tuesday => "TU",
            DayOfWeek.Wednesday => "WE",
            DayOfWeek.Thursday => "TH",
            DayOfWeek.Friday => "FR",
            DayOfWeek.Saturday => "SA",
            _ => "SU",
        };

        foreach (var entry in byDayText.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // 支持 "MO" 与 "2TU" / "-1FR" 两种写法
            var weekdayPart = entry.Length <= 2 ? entry : entry[^2..];
            if (weekdayPart != startCode)
            {
                continue;
            }

            if (entry.Length <= 2)
            {
                return true;
            }

            var ordinalText = entry[..^2];
            if (!int.TryParse(ordinalText, out var ordinal))
            {
                return true;
            }

            var occurrenceIndex = ((start.Day - 1) / 7) + 1;
            var daysInMonth = DateTime.DaysInMonth(start.Year, start.Month);
            var fromEnd = -(((daysInMonth - start.Day) / 7) + 1);

            if (ordinal == occurrenceIndex || ordinal == fromEnd)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeWeekly(IReadOnlyDictionary<string, string> parts, int interval)
    {
        if (!parts.TryGetValue("BYDAY", out var byDay))
        {
            return interval == 1 ? "每周" : $"每 {interval} 周";
        }

        var days = byDay.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (days.Length == 5 && days.All(d => d is "MO" or "TU" or "WE" or "TH" or "FR"))
        {
            return "每个工作日";
        }

        var names = string.Join("、", days.Select(WeekDayName));
        return interval == 1 ? $"每周{names}" : $"每 {interval} 周的{names}";
    }

    private static string DescribeMonthly(IReadOnlyDictionary<string, string> parts, int interval)
    {
        var prefix = interval == 1 ? "每月" : $"每 {interval} 个月的";

        if (parts.TryGetValue("BYMONTHDAY", out var monthDay))
        {
            return monthDay == "-1" ? "每月最后一天" : $"{prefix} {monthDay} 日";
        }

        if (parts.TryGetValue("BYDAY", out var byDay))
        {
            // 形如 2TU（每月第 2 个周二）或 -1FR（每月最后一个周五）
            var match = Regex.Match(byDay, @"^(-?\d)([A-Z]{2})$");
            if (match.Success)
            {
                var ordinal = match.Groups[1].Value;
                var label = ordinal.StartsWith('-') ? "最后" : $"第 {ordinal} ";
                return $"{prefix}{label}个{WeekDayName(match.Groups[2].Value)}";
            }
        }

        return interval == 1 ? "每月" : $"每 {interval} 个月";
    }

    private static string WeekDayName(string code) => code switch
    {
        "MO" => "一",
        "TU" => "二",
        "WE" => "三",
        "TH" => "四",
        "FR" => "五",
        "SA" => "六",
        "SU" => "日",
        _ => code,
    };

    private static string BuildIcs(DateTimeOffset start, string rrule)
    {
        var builder = new StringBuilder();
        builder.AppendLine("BEGIN:VCALENDAR");
        builder.AppendLine("VERSION:2.0");
        builder.AppendLine("PRODID:-//StickyTodo//Recurrence//EN");
        builder.AppendLine("BEGIN:VEVENT");
        builder.AppendLine("UID:stickytodo@local");
        builder.AppendLine($"DTSTART:{ToFloating(start):yyyyMMddTHHmmss}");
        builder.AppendLine($"DTEND:{ToFloating(start.AddMinutes(1)):yyyyMMddTHHmmss}");
        builder.AppendLine($"RRULE:{rrule}");
        builder.AppendLine("END:VEVENT");
        builder.AppendLine("END:VCALENDAR");
        return builder.ToString();
    }

    /// <summary>把本地时间转成 iCalendar 的「浮动时间」，避免带时区带来的偏移问题。</summary>
    private static DateTime ToFloating(DateTimeOffset value) =>
        DateTime.SpecifyKind(value.LocalDateTime, DateTimeKind.Unspecified);

    private static DateTimeOffset ToLocalDateTimeOffset(DateTime value)
    {
        var floating = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTimeOffset(floating, TimeZoneInfo.Local.GetUtcOffset(floating));
    }
}