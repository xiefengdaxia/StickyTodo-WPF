using Microsoft.Data.Sqlite;
using StickyTodo.Core.Entities;
using StickyTodo.Infrastructure.Token.Agents;

namespace StickyTodo.Tests;

/// <summary>
/// Alma 自己的 agent_op_traces 口径：input_tokens 是整段提示词，cached / cache_write 是它的子集
/// （total_tokens = input + output）。扣掉缓存才算「新鲜输入」，否则输入翻倍、Total 里缓存重复计。
/// </summary>
public sealed class AlmaTokenSourceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"alma-usage-{Guid.NewGuid():N}.sqlite");

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
    public void InputExcludesCache_AndStepsComeFromTrace()
    {
        CreateDatabase();
        InsertRow(DateTimeOffset.Now, input: 1_643_460, output: 2_232, reasoning: 842,
            cacheRead: 1_310_848, cacheWrite: 0, steps: 5);

        var source = new AlmaAgentSource(_dbPath);
        var entry = Assert.Single(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now)));

        Assert.Equal(1_643_460 - 1_310_848, entry.Usage.Input);
        Assert.Equal(2_232, entry.Usage.Output);
        Assert.Equal(842, entry.Usage.Reasoning);
        Assert.Equal(1_310_848, entry.Usage.CacheRead);
        Assert.Equal(0, entry.Usage.CacheWrite);
        Assert.Equal(5, entry.Steps);
        Assert.Equal("magpie:auto/deepseek", entry.ModelId);
    }

    [Fact]
    public void ZeroTokenRows_AreSkipped()
    {
        CreateDatabase();
        InsertRow(DateTimeOffset.Now, input: 0, output: 0, reasoning: 0, cacheRead: 0, cacheWrite: 0, steps: 0);

        var source = new AlmaAgentSource(_dbPath);
        Assert.Empty(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now)));
    }

    [Fact]
    public void RowsBeforeFromDay_AreFiltered()
    {
        CreateDatabase();
        InsertRow(DateTimeOffset.Now.AddDays(-10), input: 500, output: 50, reasoning: 0,
            cacheRead: 0, cacheWrite: 0, steps: 1);
        InsertRow(DateTimeOffset.Now, input: 700, output: 70, reasoning: 0,
            cacheRead: 0, cacheWrite: 0, steps: 1);

        var source = new AlmaAgentSource(_dbPath);
        var entry = Assert.Single(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now)));
        Assert.Equal(700, entry.Usage.Input);
    }

    [Fact]
    public void MissingDatabase_IsUnavailable_AndReturnsEmpty()
    {
        var source = new AlmaAgentSource(_dbPath);
        Assert.False(source.IsAvailable);
        Assert.Empty(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now)));
    }

    [Fact]
    public void BrokenDatabase_ReturnsEmptyInsteadOfThrowing()
    {
        File.WriteAllText(_dbPath, "not a sqlite file");

        var source = new AlmaAgentSource(_dbPath);
        Assert.Empty(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now)));
    }

    private void CreateDatabase()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE agent_op_traces (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                model TEXT,
                provider_id TEXT,
                step_count INTEGER NOT NULL DEFAULT 0,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
                reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                started_at TEXT NOT NULL
            )
            """;
        command.ExecuteNonQuery();
    }

    private void InsertRow(DateTimeOffset startedAt, long input, long output, long reasoning,
        long cacheRead, long cacheWrite, long steps)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_op_traces
                (id, message_id, thread_id, model, provider_id, step_count,
                 input_tokens, output_tokens, cached_input_tokens, cache_write_input_tokens,
                 reasoning_tokens, started_at)
            VALUES
                ($id, 'm1', 't1', 'magpie:auto/deepseek', 'magpie', $steps,
                 $input, $output, $cacheRead, $cacheWrite, $reasoning, $startedAt)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$steps", steps);
        command.Parameters.AddWithValue("$input", input);
        command.Parameters.AddWithValue("$output", output);
        command.Parameters.AddWithValue("$cacheRead", cacheRead);
        command.Parameters.AddWithValue("$cacheWrite", cacheWrite);
        command.Parameters.AddWithValue("$reasoning", reasoning);
        command.Parameters.AddWithValue("$startedAt",
            startedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        command.ExecuteNonQuery();
    }
}
