using System.Globalization;
using System.Text.Json;

namespace StickyTodo.Core.Calendar;

/// <summary>
/// 一条「导入」的假期安排：AI 通过 MCP（import_holidays）写进设置键
/// <see cref="HolidayImportStore.SettingKey"/>，查询时优先于
/// <see cref="ChineseHolidays"/> 里的内置数据——落在同一段日期区间就以导入的为准。
/// </summary>
public sealed record ImportedHoliday(string Name, DateOnly From, DateOnly To, DayKind Kind)
{
    /// <summary>区间的最后一天：入参写反了（to &lt; from）就当成单日。</summary>
    public DateOnly End => To < From ? From : To;

    /// <summary>这一天在不在区间里。</summary>
    public bool Covers(DateOnly date) => date >= From && date <= End;

    /// <summary>MCP 里用的字符串形式：holiday / makeup / workday。</summary>
    public static string KindTextOf(DayKind kind) => kind switch
    {
        DayKind.Holiday => "holiday",
        DayKind.MakeupWorkday => "makeup",
        _ => "workday",
    };

    /// <summary>认不出的 kind 一律当普通工作日（不猜）。</summary>
    public static DayKind ParseKind(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "holiday" or "休" => DayKind.Holiday,
        "makeup" or "makeupworkday" or "班" => DayKind.MakeupWorkday,
        _ => DayKind.Workday,
    };
}

/// <summary>
/// 某一年里的一条安排（给 MCP 的 get_holidays 用）：这一天是什么性质、叫什么、
/// 是不是来自导入。内置的假期按天逐条展开，所以一天就是一条。
/// </summary>
public sealed record HolidayEntry(DateOnly Date, string Name, DayKind Kind, bool Imported);

/// <summary>
/// 导入节假日的读写：内容是一小段 JSON，存在设置表的
/// <see cref="SettingKey"/> 键下（和别的偏好一样走 AppSettingsService）。
/// App、MCP、测试都只经过这里，格式只有一处定义。
///
/// JSON 形如：<c>[{"name":"春节","from":"2026-02-15","to":"2026-02-23","kind":"holiday"}]</c>。
/// 日期用 yyyy-MM-dd 字符串而不是 DateOnly：Core 同时要跑 net6.0（Windows 7），
/// 那边 System.Text.Json 还不认识 DateOnly。
/// </summary>
public static class HolidayImportStore
{
    /// <summary>设置表里的键名（MCP 的 import_holidays 写的就是它）。</summary>
    public const string SettingKey = "calendar.holidays";

    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>读一段 JSON；空串、坏 JSON 都当成「没有导入」，绝不让日历因此报错。</summary>
    public static IReadOnlyList<ImportedHoliday> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var rows = JsonSerializer.Deserialize<List<Row>>(json);
            if (rows is null)
            {
                return [];
            }

            var entries = new List<ImportedHoliday>(rows.Count);
            foreach (var row in rows)
            {
                if (row is null || !TryParseDate(row.from, out var from))
                {
                    continue;
                }

                var to = TryParseDate(row.to, out var end) ? end : from;
                var kind = ImportedHoliday.ParseKind(row.kind);
                var name = string.IsNullOrWhiteSpace(row.name) ? ImportedHoliday.KindTextOf(kind) : row.name.Trim();
                entries.Add(new ImportedHoliday(name, from, to < from ? from : to, kind));
            }

            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>写成 JSON；空集合写成空串，等于「清掉导入，回到内置数据」。</summary>
    public static string Serialize(IEnumerable<ImportedHoliday>? entries)
    {
        if (entries is null)
        {
            return string.Empty;
        }

        var rows = entries
            .Where(entry => entry is not null)
            .Select(entry => new Row
            {
                name = entry.Name,
                from = entry.From.ToString(DateFormat, CultureInfo.InvariantCulture),
                to = entry.End.ToString(DateFormat, CultureInfo.InvariantCulture),
                kind = ImportedHoliday.KindTextOf(entry.Kind),
            })
            .ToList();

        return rows.Count == 0 ? string.Empty : JsonSerializer.Serialize(rows);
    }

    private static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>JSON 里的字段名（小写、短），对外就是 MCP 入参那一套。</summary>
    private sealed class Row
    {
        public string? name { get; set; }

        public string? from { get; set; }

        public string? to { get; set; }

        public string? kind { get; set; }
    }
}
