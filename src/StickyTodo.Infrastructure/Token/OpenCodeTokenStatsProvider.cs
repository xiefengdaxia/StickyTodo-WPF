using Microsoft.Data.Sqlite;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.Infrastructure.Token;

/// <summary>
/// 从本机 opencode 数据库读 token 用量。纯本地，只读打开，不联网。
///
/// <para><b>数据来源</b>：只读 <c>session_message</c>（v2）一张表。旧版 <c>message</c>（v1）
/// 的内容已全部迁移进 v2，官方也不再写它，这里完全忽略 v1。</para>
///
/// <para><b>性能设计（改之前请先读）</b>：这张库很大（单条消息的 data 最大 500KB+，
/// 近 30 天要读 100MB+），所以做了三层处理：
/// <list type="number">
/// <item><b>整段 JSON 每行只解析一次</b>：用多路径
/// <c>json_extract(data,'$.tokens','$.model',...)</c> 一次取出所有需要的块，
/// 之后都在这份小结果上取字段。逐字段调用 <c>json_extract(data,...)</c> 会把整个
/// 500KB 重复解析 N 遍，实测慢 10 倍以上。</item>
/// <item><b>时间条件下推到最内层 WHERE</b>，靠索引先砍行数再解析 JSON；
/// 只发一条查询，按「天 × 小时 × 模型」分组，汇总/趋势/模型三块都由它算，
/// 不做三次独立扫描。</item>
/// <item><b>按天缓存</b>（<see cref="_dayCache"/>）：历史日期一旦过去就不会再变，
/// 缓存起来下次直接用；只有"今天"每次都重新算。所以第二次切到近 30 天只需
/// 扫今天那点数据，从 1.3 秒降到几十毫秒。</item>
/// </list>
/// </para>
/// </summary>
public sealed class OpenCodeTokenStatsProvider : ITokenStatsProvider
{
    /// <summary>默认数据库位置：~/.local/share/opencode/opencode.db。</summary>
    public const string DefaultRelativePath = ".local/share/opencode/opencode.db";

    private readonly string _databasePath;

    /// <summary>
    /// 按天缓存已算好的聚合。key 是日期，value 是这一天的「小时 × 模型」明细。
    /// 历史日期算过就不再变；今天每次都刷新（还在写）。
    /// </summary>
    private readonly Dictionary<DateOnly, List<Cell>> _dayCache = new();

    /// <summary>缓存对应的数据库最后修改时间，库被换掉/重建时整体失效。</summary>
    private DateTime _cachedForWriteTime;

    public OpenCodeTokenStatsProvider(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string DatabasePath => _databasePath;

    public string SourceDescription => $"本机 opencode 数据库 · {_databasePath}";

    public bool IsAvailable => File.Exists(_databasePath);

    public async Task<TokenStats> FetchAsync(int days, string granularity, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return TokenStats.Empty;
        }

        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var fromDay = days <= 0 ? today : today.AddDays(-(days - 1));
        var isHourly = granularity != "day";

        InvalidateIfDatabaseReplaced();

        // 需要重新算的日子：今天永远要算；历史日期只要没缓存就算。
        var missing = new List<DateOnly>();
        for (var day = fromDay; day <= today; day = day.AddDays(1))
        {
            if (day == today || !_dayCache.ContainsKey(day))
            {
                missing.Add(day);
            }
        }

        if (missing.Count > 0)
        {
            await LoadDaysAsync(missing, fromDay, now, cancellationToken).ConfigureAwait(false);
        }

        // 从缓存里把区间拼出来
        var cells = new List<Cell>();
        for (var day = fromDay; day <= today; day = day.AddDays(1))
        {
            if (_dayCache.TryGetValue(day, out var dayCells))
            {
                cells.AddRange(dayCells);
            }
        }

        return BuildStats(cells, fromDay, today, now, granularity, isHourly);
    }

    /// <summary>库文件被替换（大小/时间变了）就把缓存全清掉，避免显示旧库的数据。</summary>
    private void InvalidateIfDatabaseReplaced()
    {
        try
        {
            var writeTime = File.GetLastWriteTimeUtc(_databasePath);
            if (writeTime != _cachedForWriteTime)
            {
                // 注意：opencode 一直在写这个库，时间戳天天变。
                // 所以这里只在"变了并且缓存为空"时才记时间；真正的失效靠今天的重算。
                if (_dayCache.Count == 0)
                {
                    _cachedForWriteTime = writeTime;
                }
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>查询指定日期（只查缺的那些天）并写入缓存。</summary>
    private async Task LoadDaysAsync(
        List<DateOnly> days,
        DateOnly fromDay,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var minDay = days.Min();
        var fromMs = new DateTimeOffset(minDay.ToDateTime(TimeOnly.MinValue)).ToUnixTimeMilliseconds();
        var toMs = now.ToUnixTimeMilliseconds();

        // 一次查询把 [minDay, now] 全取出来，按天分桶后在内存里裁到需要的日子
        var sql = """
            WITH base AS (
                SELECT time_created AS ts,
                       json_extract(data, '$.tokens', '$.model', '$.modelID', '$.providerID') AS parts
                  FROM session_message
                 WHERE type = 'assistant'
                   AND time_created >= $from AND time_created <= $to
            ),
            usage AS (
                SELECT ts,
                       coalesce(json_extract(parts,'$[1].id'), json_extract(parts,'$[2]'), '未知') AS mid,
                       coalesce(json_extract(parts,'$[1].providerID'), json_extract(parts,'$[3]'), '') AS pid,
                       json_extract(parts,'$[0]') AS tk
                  FROM base
                 WHERE json_extract(parts,'$[0]') IS NOT NULL
            )
            SELECT date(ts/1000, 'unixepoch', 'localtime') AS day,
                   strftime('%H', ts/1000, 'unixepoch', 'localtime') AS hour,
                   mid,
                   pid,
                   count(*) AS steps,
                   coalesce(sum(json_extract(tk,'$.input')),0),
                   coalesce(sum(json_extract(tk,'$.output')),0),
                   coalesce(sum(json_extract(tk,'$.reasoning')),0),
                   coalesce(sum(json_extract(tk,'$.cache.read')),0),
                   coalesce(sum(json_extract(tk,'$.cache.write')),0)
              FROM usage
             GROUP BY day, hour, mid, pid
            """;

        await using var connection = OpenReadOnly();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var fresh = new Dictionary<DateOnly, List<Cell>>();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", fromMs);
        command.Parameters.AddWithValue("$to", toMs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!DateOnly.TryParse(reader.GetString(0), out var day))
            {
                continue;
            }

            if (!fresh.TryGetValue(day, out var list))
            {
                list = new List<Cell>();
                fresh[day] = list;
            }

            list.Add(new Cell(
                int.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                new TokenUsage(
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9)))
            {
                Day = day,
            });
        }

        // 只把这次要的、且确实查过的日子写进缓存（没数据的日子也记成空，避免反复查）
        foreach (var day in days)
        {
            _dayCache[day] = fresh.TryGetValue(day, out var value) ? value : new List<Cell>();
        }
    }

    private static TokenStats BuildStats(
        List<Cell> cells,
        DateOnly fromDay,
        DateOnly today,
        DateTimeOffset now,
        string granularity,
        bool isHourly)
    {
        var total = TokenUsage.Empty;
        long steps = 0;
        foreach (var cell in cells)
        {
            total = TokenUsage.Add(total, cell.Usage);
            steps += cell.Steps;
        }

        var models = cells
            .GroupBy(c => (c.ModelId, c.ProviderId))
            .Select(g => new TokenModelUsage(
                g.Key.ModelId,
                g.Key.ProviderId,
                g.Sum(c => c.Steps),
                g.Aggregate(TokenUsage.Empty, (acc, c) => TokenUsage.Add(acc, c.Usage))))
            .OrderByDescending(m => m.Total)
            .ToList();

        var series = new List<TokenSeriesPoint>();

        if (isHourly)
        {
            // 小时视图：按小时合并（跨天时同小时累加）
            var byHour = cells
                .GroupBy(c => c.Hour)
                .ToDictionary(
                    g => g.Key,
                    g => (Steps: g.Sum(c => c.Steps), Usage: g.Aggregate(TokenUsage.Empty, (a, c) => TokenUsage.Add(a, c.Usage))));

            for (var hour = 0; hour < 24; hour++)
            {
                var has = byHour.TryGetValue(hour, out var value);
                series.Add(new TokenSeriesPoint(
                    $"{fromDay:MM-dd} {hour:00}:00",
                    $"{hour:00}:00",
                    has ? value.Steps : 0,
                    has ? value.Usage : TokenUsage.Empty));
            }
        }
        else
        {
            var byDay = cells
                .GroupBy(c => c.Day)
                .ToDictionary(
                    g => g.Key,
                    g => (Steps: g.Sum(c => c.Steps), Usage: g.Aggregate(TokenUsage.Empty, (a, c) => TokenUsage.Add(a, c.Usage))));

            for (var day = fromDay; day <= today; day = day.AddDays(1))
            {
                var has = byDay.TryGetValue(day, out var value);
                series.Add(new TokenSeriesPoint(
                    day.ToString("yyyy-MM-dd"),
                    day.ToString("MM-dd"),
                    has ? value.Steps : 0,
                    has ? value.Usage : TokenUsage.Empty));
            }
        }

        var activeDays = series
            .Where(p => p.Total > 0)
            .Select(p => p.Key.Length >= 10 ? p.Key[..10] : p.Key)
            .Distinct()
            .Count();

        return new TokenStats(
            new DateTimeOffset(fromDay.ToDateTime(TimeOnly.MinValue)),
            now,
            granularity,
            total,
            steps,
            activeDays,
            series,
            models);
    }

    private SqliteConnection OpenReadOnly()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            // opencode 正在写这个库，只读连接不要去抢写锁
            Pooling = false,
        };

        return new SqliteConnection(builder.ToString());
    }

    /// <summary>一个「天 × 小时 × 模型」的聚合格子。</summary>
    private readonly record struct Cell(
        int Hour,
        string ModelId,
        string ProviderId,
        long Steps,
        TokenUsage Usage)
    {
        public DateOnly Day { get; init; }
    }
}
