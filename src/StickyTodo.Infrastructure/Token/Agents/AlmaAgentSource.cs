using System.Globalization;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.Infrastructure.Token.Agents;

/// <summary>
/// Alma（本机 AI 助手）自己的用量账本：<c>%APPDATA%\Alma\chat_threads.db</c> 的
/// <c>agent_op_traces</c> 表，一次模型回合一行。
///
/// <para><b>口径</b>：这张表的 <c>input_tokens</c> 是「整段提示词」，
/// <c>cached_input_tokens</c> / <c>cache_write_input_tokens</c> 是它的子集
/// （<c>total_tokens = input + output</c>）。所以新鲜输入要把缓存那两块扣掉，
/// 否则既把输入算大一倍，又会在 Total 里把缓存重复加一次（和 ZCode 同一个坑）。</para>
///
/// <para>只读打开、不抢写锁：Alma 随时在写这个库。</para>
/// </summary>
internal sealed class AlmaAgentSource : IAgentTokenSource
{
    /// <summary>缓存有效期：同一区间 30 秒内不重复读库（今天的行还在增长）。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly string _databasePath;
    private readonly object _gate = new();
    private DateOnly? _cachedFrom;
    private DateTime _cachedAt;
    private List<AgentUsageEntry>? _cached;

    /// <summary>默认读 %APPDATA%\Alma\chat_threads.db；测试可传别的路径。</summary>
    public AlmaAgentSource(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Alma",
            "chat_threads.db");
    }

    public string AgentId => "alma";

    public string DisplayName => "Alma";

    public string SourceDescription => $"Alma · {_databasePath}";

    public bool IsAvailable
    {
        get
        {
            try
            {
                return File.Exists(_databasePath);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public IEnumerable<AgentUsageEntry> ReadEntries(DateOnly fromDay)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (fromDay > today)
        {
            return [];
        }

        lock (_gate)
        {
            if (_cached is not null &&
                _cachedFrom == fromDay &&
                DateTime.UtcNow - _cachedAt < CacheTtl)
            {
                return _cached;
            }

            List<AgentUsageEntry> entries;
            try
            {
                entries = ReadFromDatabase(fromDay);
            }
            catch (Exception)
            {
                // 库被占用 / 版本不对 / 表还没建：这一轮当作没有数据，不影响其它源
                return [];
            }

            _cached = entries;
            _cachedFrom = fromDay;
            _cachedAt = DateTime.UtcNow;
            return entries;
        }
    }

    private List<AgentUsageEntry> ReadFromDatabase(DateOnly fromDay)
    {
        var entries = new List<AgentUsageEntry>();
        var from = fromDay.ToDateTime(TimeOnly.MinValue).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
                Pooling = false, // Alma 正在写这个库，只读连接别去抢写锁
            }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT started_at,
                   coalesce(model, ''),
                   coalesce(provider_id, ''),
                   coalesce(input_tokens, 0),
                   coalesce(output_tokens, 0),
                   coalesce(reasoning_tokens, 0),
                   coalesce(cached_input_tokens, 0),
                   coalesce(cache_write_input_tokens, 0),
                   coalesce(step_count, 0)
              FROM agent_op_traces
             WHERE started_at >= $from
            """;
        command.Parameters.AddWithValue("$from", from);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var input = reader.GetInt64(3);
            var output = reader.GetInt64(4);
            var reasoning = reader.GetInt64(5);
            var cacheRead = reader.GetInt64(6);
            var cacheWrite = reader.GetInt64(7);
            var steps = reader.GetInt64(8);

            // 鉴权失败 / 全零行不是一次有效调用
            if (input + output + reasoning + cacheRead + cacheWrite == 0)
            {
                continue;
            }

            var cacheTotal = cacheRead + cacheWrite;
            var freshInput = input >= cacheTotal ? input - cacheTotal : 0;

            var timestamp = DateTimeOffset.TryParse(
                reader.GetString(0),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed.ToLocalTime()
                : DateTimeOffset.Now;

            entries.Add(new AgentUsageEntry(
                timestamp,
                reader.GetString(1),
                reader.GetString(2),
                new TokenUsage(freshInput, output, reasoning, cacheRead, cacheWrite),
                Math.Max(1, steps)));
        }

        return entries;
    }
}
