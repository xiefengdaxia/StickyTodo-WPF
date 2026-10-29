using Microsoft.Data.Sqlite;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.Infrastructure.Token;

/// <summary>
/// 把 opencode 的本地账本（~/.local/share/opencode/opencode.db → session_message）
/// 适配成 <see cref="IAgentTokenSource"/>。
///
/// <para>与 <see cref="OpenCodeTokenStatsProvider"/> 的关系：那个类为看板直查做了
/// 重度优化（多路径 json_extract、按天缓存），但产出的是聚合结果；
/// 多 Agent 聚合需要「天 × 小时 × 模型」的格子粒度，这里用同一条 SQL 的一个变体
/// 直接分组取格子（也是一次扫描、时间条件下推），缓存策略相同。</para>
/// </summary>
public sealed class OpenCodeAgentTokenSource : IAgentTokenSource
{
    private readonly string _databasePath;

    /// <summary>按天缓存：日期 → 格子。历史日期不变，只重算今天（与 OpenCodeTokenStatsProvider 一致）。</summary>
    private readonly Dictionary<DateOnly, List<AgentUsageEntry>> _dayCache = new();

    public OpenCodeAgentTokenSource(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OpenCodeTokenStatsProvider.DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string AgentId => "opencode";
    public string DisplayName => "OpenCode";
    public string SourceDescription => $"本机 opencode 数据库 · {_databasePath}";
    public bool IsAvailable => File.Exists(_databasePath);

    public IEnumerable<AgentUsageEntry> ReadEntries(DateOnly fromDay)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (fromDay > today)
        {
            return [];
        }

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
            LoadDays(missing);
        }

        var result = new List<AgentUsageEntry>();
        for (var day = fromDay; day <= today; day = day.AddDays(1))
        {
            if (_dayCache.TryGetValue(day, out var entries))
            {
                result.AddRange(entries);
            }
        }

        return result;
    }

    private void LoadDays(List<DateOnly> days)
    {
        var minDay = days.Min();
        var fromMs = new DateTimeOffset(minDay.ToDateTime(TimeOnly.MinValue)).ToUnixTimeMilliseconds();
        var toMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        // 与 OpenCodeTokenStatsProvider 同一条查询骨架：整段 JSON 一次取出、
        // 时间条件下推、按「天 × 小时 × 模型」分组。
        // data.model 是对象（$.model.id / $.model.providerID），老字段 $.modelID 是历史残留。
        var sql = """
            WITH base AS (
                SELECT time_created AS ts,
                       json_extract(data, '$.tokens', '$.model') AS parts
                  FROM session_message
                 WHERE type = 'assistant'
                   AND time_created >= $from AND time_created <= $to
            ),
            usage AS (
                SELECT ts,
                       coalesce(json_extract(parts,'$[1].id'), '未知') AS mid,
                       coalesce(json_extract(parts,'$[1].providerID'), '') AS pid,
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

        var fresh = new Dictionary<DateOnly, List<AgentUsageEntry>>();

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$from", fromMs);
            command.Parameters.AddWithValue("$to", toMs);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!DateOnly.TryParse(reader.GetString(0), out var day))
                {
                    continue;
                }

                var entry = new AgentUsageEntry(
                    // 格子粒度：时间戳取该日该小时的起点（聚合按天/小时分桶，起点足够）
                    new DateTimeOffset(day.ToDateTime(new TimeOnly(int.Parse(reader.GetString(1)), 0))),
                    reader.GetString(2),
                    reader.GetString(3),
                    new TokenUsage(
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9)),
                    // 这一格里有几次 assistant 消息 = 几次模型调用
                    reader.GetInt64(4));

                if (!fresh.TryGetValue(day, out var list))
                {
                    list = [];
                    fresh[day] = list;
                }

                list.Add(entry);
            }
        }
        catch (SqliteException)
        {
            // 库正在被 opencode 写 / 迁移中：这轮拿不到就当空，下次重算
        }
        catch (IOException)
        {
        }

        foreach (var day in days)
        {
            _dayCache[day] = fresh.TryGetValue(day, out var value) ? value : [];
        }
    }
}
