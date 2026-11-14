using Microsoft.Data.Sqlite;
using StickyTodo.Core.Entities;
using StickyTodo.Infrastructure.Token.Agents;

namespace StickyTodo.Tests;

/// <summary>
/// ZCode 的 model_usage 表口径：input_tokens 是「整段提示词」，缓存读/写已经包含在里面
/// （raw_usage_json: totalTokens = inputTokens + outputTokens，且 inputTokens ≥ cacheReadTokens）。
/// 直接当独立桶会在「输入」上算大一倍、Total 里再把缓存重复加一次。
/// </summary>
public sealed class ZcodeTokenSourceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"zcode-usage-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void InputTokens_ExcludeCacheReadAndWrite()
    {
        var startedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        CreateDatabase();
        InsertRow(startedAt, input: 673_950, output: 2_676, cacheRead: 670_848, cacheWrite: 0);

        var source = new ZcodeAgentSource(_dbPath);
        var usage = Sum(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now)));

        // 新鲜输入 = 673950 - 670848；缓存读单列；Total 与 provider 的 totalTokens 一致
        Assert.Equal(3_102, usage.Input);
        Assert.Equal(2_676, usage.Output);
        Assert.Equal(670_848, usage.CacheRead);
        Assert.Equal(0, usage.CacheWrite);
        Assert.Equal(676_626, usage.Total);
    }

    [Fact]
    public void InputTokens_SmallerThanCache_DoesNotGoNegative()
    {
        CreateDatabase();
        InsertRow(DateTimeOffset.Now.ToUnixTimeMilliseconds(), input: 100, output: 10, cacheRead: 500, cacheWrite: 0);

        var source = new ZcodeAgentSource(_dbPath);
        var usage = Sum(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now)));

        Assert.Equal(0, usage.Input);
        Assert.Equal(510, usage.Total);
    }

    [Fact]
    public void AllZeroRow_IsSkipped()
    {
        CreateDatabase();
        InsertRow(DateTimeOffset.Now.ToUnixTimeMilliseconds(), input: 0, output: 0, cacheRead: 0, cacheWrite: 0);

        var source = new ZcodeAgentSource(_dbPath);
        Assert.Empty(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now)));
    }

    private static TokenUsage Sum(IEnumerable<Core.Abstractions.AgentUsageEntry> entries) =>
        entries.Aggregate(TokenUsage.Empty, (acc, e) => TokenUsage.Add(acc, e.Usage));

    private void CreateDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE model_usage (
                started_at INTEGER NOT NULL,
                model_id TEXT,
                provider_id TEXT,
                attempt_index INTEGER NOT NULL DEFAULT 0,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_input_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_input_tokens INTEGER NOT NULL DEFAULT 0
            );
            """;
        command.ExecuteNonQuery();
    }

    private void InsertRow(long startedAt, long input, long output, long cacheRead, long cacheWrite)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO model_usage
                (started_at, model_id, provider_id, attempt_index,
                 input_tokens, output_tokens, reasoning_tokens,
                 cache_read_input_tokens, cache_creation_input_tokens)
            VALUES ($t, 'GLM-5.3-Flash', 'account:bigmodel-start-plan', 0,
                    $i, $o, 0, $cr, $cw);
            """;
        command.Parameters.AddWithValue("$t", startedAt);
        command.Parameters.AddWithValue("$i", input);
        command.Parameters.AddWithValue("$o", output);
        command.Parameters.AddWithValue("$cr", cacheRead);
        command.Parameters.AddWithValue("$cw", cacheWrite);
        command.ExecuteNonQuery();
    }
}
