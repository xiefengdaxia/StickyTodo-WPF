using Microsoft.Data.Sqlite;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.Infrastructure.Token;

/// <summary>
/// 从本机 opencode 数据库读 token 用量。纯本地，只读打开，不联网。
///
/// 数据库里有两套消息表，必须分清：
///   * <c>session_message</c>（v2）：现存的主表，覆盖全部历史 + 当前。
///   * <c>message</c>（v1）：v2 迁移的来源，内容与 v2 大量重复。
/// 所以**不能简单相加**，否则 6–9 月会被统计两遍。做法是：
///   以 v2 为准，再补上 v1 里 id 不存在于 v2 的那些（实测约 125 条早期漏迁消息）。
/// </summary>
public sealed class OpenCodeTokenStatsProvider : ITokenStatsProvider
{
    /// <summary>默认数据库位置：%LOCALAPPDATA% 同级惯例的 ~/.local/share/opencode/opencode.db。</summary>
    public const string DefaultRelativePath = ".local/share/opencode/opencode.db";

    private readonly string _databasePath;

    public OpenCodeTokenStatsProvider(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string DatabasePath => _databasePath;

    public string SourceDescription => $"本机 opencode 数据库 · {_databasePath}";

    public bool IsAvailable => File.Exists(_databasePath);

    /// <summary>
    /// 合并两侧消息的公共 CTE。
    /// 注意：<c>json_extract</c> 在旧版 SQLite 上对不存在的路径会报错，
    /// 但本项目依赖 .NET 自带的 SQLite（3.4x），支持 <c>-&gt;</c> 与 json_extract。
    /// </summary>
    private const string UnionCte = """
        WITH usage AS (
            SELECT s.time_created AS ts, s.id AS mid, s.data AS d
              FROM session_message s
             WHERE s.type = 'assistant'
               AND json_extract(s.data, '$.tokens') IS NOT NULL
            UNION ALL
            SELECT m.time_created, m.id, m.data
              FROM message m
             WHERE json_extract(m.data, '$.role') = 'assistant'
               AND json_extract(m.data, '$.tokens') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM session_message s2 WHERE s2.id = m.id)
        )
        """;

    public async Task<TokenStats> FetchAsync(int days, string granularity, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return TokenStats.Empty;
        }

        var now = DateTimeOffset.Now;
        var fromLocal = days <= 0
            ? now.Date
            : now.Date.AddDays(-(days - 1));

        var fromMs = new DateTimeOffset(fromLocal).ToUnixTimeMilliseconds();
        var toMs = now.ToUnixTimeMilliseconds();

        await using var connection = OpenReadOnly();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var total = await ReadTotalAsync(connection, fromMs, toMs, cancellationToken).ConfigureAwait(false);
        var series = granularity == "day"
            ? await ReadDailyAsync(connection, fromLocal, now.DateTime, fromMs, toMs, cancellationToken).ConfigureAwait(false)
            : await ReadHourlyAsync(connection, fromLocal, fromMs, toMs, cancellationToken).ConfigureAwait(false);
        var models = await ReadModelsAsync(connection, fromMs, toMs, cancellationToken).ConfigureAwait(false);

        var steps = series.Sum(p => p.Steps);
        var activeDays = series
            .Where(p => p.Total > 0)
            .Select(p => p.Key.Length >= 10 ? p.Key[..10] : p.Key)
            .Distinct()
            .Count();

        return new TokenStats(
            new DateTimeOffset(fromLocal),
            now,
            granularity,
            total.Usage,
            total.Steps,
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

    private static async Task<(TokenUsage Usage, long Steps)> ReadTotalAsync(
        SqliteConnection connection,
        long fromMs,
        long toMs,
        CancellationToken cancellationToken)
    {
        var sql = UnionCte + """

            SELECT count(*) AS steps,
                   coalesce(sum(json_extract(d,'$.tokens.input')),0),
                   coalesce(sum(json_extract(d,'$.tokens.output')),0),
                   coalesce(sum(json_extract(d,'$.tokens.reasoning')),0),
                   coalesce(sum(json_extract(d,'$.tokens.cache.read')),0),
                   coalesce(sum(json_extract(d,'$.tokens.cache.write')),0)
              FROM usage
             WHERE ts >= $from AND ts <= $to
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", fromMs);
        command.Parameters.AddWithValue("$to", toMs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (TokenUsage.Empty, 0);
        }

        return (
            new TokenUsage(
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5)),
            reader.GetInt64(0));
    }

    private static async Task<List<TokenSeriesPoint>> ReadHourlyAsync(
        SqliteConnection connection,
        DateTime fromLocal,
        long fromMs,
        long toMs,
        CancellationToken cancellationToken)
    {
        var sql = UnionCte + """

            SELECT strftime('%H', ts/1000, 'unixepoch', 'localtime') AS bucket,
                   count(*) AS steps,
                   coalesce(sum(json_extract(d,'$.tokens.input')),0),
                   coalesce(sum(json_extract(d,'$.tokens.output')),0),
                   coalesce(sum(json_extract(d,'$.tokens.reasoning')),0),
                   coalesce(sum(json_extract(d,'$.tokens.cache.read')),0),
                   coalesce(sum(json_extract(d,'$.tokens.cache.write')),0)
              FROM usage
             WHERE ts >= $from AND ts <= $to
             GROUP BY bucket
            """;

        // 先查出来放进字典，再补齐 0–23 点，保证图表横轴完整
        var found = new Dictionary<int, (long Steps, TokenUsage Usage)>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Parameters.AddWithValue("$from", fromMs);
            command.Parameters.AddWithValue("$to", toMs);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var hour = int.Parse(reader.GetString(0));
                found[hour] = (
                    reader.GetInt64(1),
                    new TokenUsage(
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6)));
            }
        }

        var list = new List<TokenSeriesPoint>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            var has = found.TryGetValue(hour, out var value);
            var date = fromLocal.Date;
            var key = $"{date:MM-dd} {hour:00}:00";

            list.Add(new TokenSeriesPoint(
                key,
                $"{hour:00}:00",
                has ? value.Steps : 0,
                has ? value.Usage : TokenUsage.Empty));
        }

        return list;
    }

    private static async Task<List<TokenSeriesPoint>> ReadDailyAsync(
        SqliteConnection connection,
        DateTime fromLocal,
        DateTime toLocal,
        long fromMs,
        long toMs,
        CancellationToken cancellationToken)
    {
        var sql = UnionCte + """

            SELECT date(ts/1000, 'unixepoch', 'localtime') AS bucket,
                   count(*) AS steps,
                   coalesce(sum(json_extract(d,'$.tokens.input')),0),
                   coalesce(sum(json_extract(d,'$.tokens.output')),0),
                   coalesce(sum(json_extract(d,'$.tokens.reasoning')),0),
                   coalesce(sum(json_extract(d,'$.tokens.cache.read')),0),
                   coalesce(sum(json_extract(d,'$.tokens.cache.write')),0)
              FROM usage
             WHERE ts >= $from AND ts <= $to
             GROUP BY bucket
             ORDER BY bucket
            """;

        var found = new Dictionary<string, (long Steps, TokenUsage Usage)>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Parameters.AddWithValue("$from", fromMs);
            command.Parameters.AddWithValue("$to", toMs);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found[reader.GetString(0)] = (
                    reader.GetInt64(1),
                    new TokenUsage(
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6)));
            }
        }

        // 补齐区间内每一天（没用的日子显示 0，趋势才有连续性）
        var list = new List<TokenSeriesPoint>();
        var start = fromLocal.Date;
        var end = toLocal.Date;

        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd");
            var has = found.TryGetValue(key, out var value);

            list.Add(new TokenSeriesPoint(
                key,
                day.ToString("MM-dd"),
                has ? value.Steps : 0,
                has ? value.Usage : TokenUsage.Empty));
        }

        return list;
    }

    /// <summary>按模型聚合。<c>model</c> 是 JSON，取 id / providerID。</summary>
    private static async Task<List<TokenModelUsage>> ReadModelsAsync(
        SqliteConnection connection,
        long fromMs,
        long toMs,
        CancellationToken cancellationToken)
    {
        var sql = UnionCte + """

            , tagged AS (
                SELECT ts, d,
                       json_extract(d,'$.model.id')         AS model_id,
                       json_extract(d,'$.model.providerID') AS provider_id,
                       json_extract(d,'$.modelID')          AS model_id_flat,
                       json_extract(d,'$.providerID')       AS provider_id_flat
                  FROM usage
                 WHERE ts >= $from AND ts <= $to
            )
            SELECT coalesce(nullif(model_id, ''), model_id_flat, '未知') AS mid,
                   coalesce(nullif(provider_id, ''), provider_id_flat, '') AS pid,
                   count(*) AS steps,
                   coalesce(sum(json_extract(d,'$.tokens.input')),0),
                   coalesce(sum(json_extract(d,'$.tokens.output')),0),
                   coalesce(sum(json_extract(d,'$.tokens.reasoning')),0),
                   coalesce(sum(json_extract(d,'$.tokens.cache.read')),0),
                   coalesce(sum(json_extract(d,'$.tokens.cache.write')),0)
              FROM tagged
             GROUP BY mid, pid
             ORDER BY steps DESC
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", fromMs);
        command.Parameters.AddWithValue("$to", toMs);

        var list = new List<TokenModelUsage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new TokenModelUsage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                new TokenUsage(
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7))));
        }

        return list;
    }
}