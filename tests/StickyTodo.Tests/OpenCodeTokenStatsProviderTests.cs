using Microsoft.Data.Sqlite;
using StickyTodo.Infrastructure.Token;

namespace StickyTodo.Tests;

/// <summary>
/// 本机 opencode 读库逻辑的测试。
/// 用临时库按真实表结构建最小数据集，重点验证：
///   1. 按小时/按天聚合必须限定在时间窗口内（曾出过"23 点包含全历史"的 bug）；
///   2. 模型取自 JSON 的 model.id / modelID 两种写法；
///   3. v1 表（message）即使存在也不再被读取。
/// </summary>
public sealed class OpenCodeTokenStatsProviderTests : IDisposable
{
    private readonly string _path;
    private readonly string _connectionString;

    public OpenCodeTokenStatsProviderTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"opencode-test-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        Execute(connection, """
            CREATE TABLE session_message (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                type TEXT NOT NULL,
                seq INTEGER NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL
            );
            CREATE TABLE message (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL
            );
            """);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void InsertV2(SqliteConnection connection, string id, DateTimeOffset at, long input, string modelJson)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_message (id, session_id, type, seq, time_created, time_updated, data)
            VALUES ($id, 'ses_1', 'assistant', 1, $ts, $ts, $data)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", at.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$data",
            $"{{\"role\":\"assistant\",\"model\":{modelJson},\"tokens\":{{\"input\":{input},\"output\":10,\"reasoning\":5,\"cache\":{{\"read\":100,\"write\":2}}}}}}");
        command.ExecuteNonQuery();
    }

    private static void InsertV1(SqliteConnection connection, string id, DateTimeOffset at, long input)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO message (id, session_id, time_created, time_updated, data)
            VALUES ($id, 'ses_1', $ts, $ts, $data)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", at.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$data",
            $"{{\"role\":\"assistant\",\"modelID\":\"v1-model\",\"providerID\":\"p\",\"tokens\":{{\"input\":{input},\"output\":10,\"reasoning\":5,\"cache\":{{\"read\":100,\"write\":2}}}}}}");
        command.ExecuteNonQuery();
    }

    private OpenCodeTokenStatsProvider CreateProvider() => new(_path);

    [Fact]
    public async Task MissingDatabase_IsReportedUnavailable()
    {
        var provider = new OpenCodeTokenStatsProvider(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.db"));

        Assert.False(provider.IsAvailable);

        var stats = await provider.FetchAsync(0, "hour");
        Assert.Equal(0, stats.Total.Total);
    }

    [Fact]
    public void ExistingDatabase_IsAvailableAndDescribesSource()
    {
        var provider = CreateProvider();

        Assert.True(provider.IsAvailable);
        Assert.Contains("opencode", provider.SourceDescription);
        Assert.Contains(_path, provider.SourceDescription);
    }

    [Fact]
    public async Task V1Table_IsIgnored()
    {
        var now = DateTimeOffset.Now;

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            InsertV2(connection, "msg_v2", now, 1000, """{"id":"m1","providerID":"p"}""");
            // v1 里塞一条 v2 没有的记录：不应被读到（内容已全部迁到 v2，v1 不再使用）
            InsertV1(connection, "msg_v1_only", now, 999_999);
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        Assert.Equal(1, stats.Steps);
        // 1000 + 10 + 5 + 100 + 2
        Assert.Equal(1117, stats.Total.Total);
        Assert.DoesNotContain(stats.Models, m => m.ModelId == "v1-model");
    }

    [Fact]
    public async Task HourlyBuckets_IgnoreOtherDays()
    {
        var today = DateTimeOffset.Now.Date;
        var yesterday = today.AddDays(-1);
        // 插入时间必须是"今天已过去的时刻"：写死钟点（00:30 / 12:30）在某些运行时刻
        // 会落到 FetchAsync 的 now 上限之外或跨到昨天（历史缺陷，午夜后跑必挂）。
        // 取"今天 00:00:01"永远安全（一天 86399 秒，测试跑得再快也在今天之内）。
        var todayMorning = today.AddSeconds(1);
        var yesterdayMorning = new DateTimeOffset(yesterday.AddSeconds(1));

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            InsertV2(connection, "msg_t", todayMorning, 500, """{"id":"m1","providerID":"p"}""");
            InsertV2(connection, "msg_y", yesterdayMorning, 999_999, """{"id":"m1","providerID":"p"}""");
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        Assert.Equal(24, stats.Series.Count);

        // 0 点桶只应包含今天的量，不能被昨天的同小时污染
        Assert.Equal(1, stats.Series[0].Steps);
        Assert.Equal(500 + 10 + 5 + 100 + 2, stats.Series[0].Total);

        Assert.All(stats.Series.Skip(1), p => Assert.Equal(0, p.Total));
    }

    [Fact]
    public async Task DailyBuckets_CoverWholeRangeAndIgnoreOutside()
    {
        var today = DateTimeOffset.Now.Date;
        var yesterdayTen = new DateTimeOffset(today.AddDays(-1).AddHours(10));
        // 插入时间用"今天 00:00:01"：永远在过去、且永远落在今天桶（见上条测试的注释）
        var todayEarly = today.AddSeconds(1);

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            InsertV2(connection, "msg_d1", yesterdayTen, 100, """{"id":"m1","providerID":"p"}""");
            InsertV2(connection, "msg_d2", todayEarly, 200, """{"id":"m1","providerID":"p"}""");
            InsertV2(connection, "msg_old", new DateTimeOffset(today.AddDays(-20)), 555_555, """{"id":"m1","providerID":"p"}""");
        }

        var stats = await CreateProvider().FetchAsync(3, "day");

        Assert.Equal(3, stats.Series.Count);
        Assert.Equal(today.AddDays(-2).ToString("yyyy-MM-dd"), stats.Series[0].Key);
        Assert.Equal(today.ToString("yyyy-MM-dd"), stats.Series[2].Key);

        // 每条 = input + 10 输出 + 5 推理 + 100 缓存读 + 2 缓存写
        Assert.Equal(217, stats.Series[1].Total);
        Assert.Equal(317, stats.Series[2].Total);
        Assert.Equal(534, stats.Series.Sum(p => p.Total));
        Assert.Equal(0, stats.Series[0].Total);
    }

    [Fact]
    public async Task Models_AreReadFromNestedAndFlatJson()
    {
        var now = DateTimeOffset.Now;

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            InsertV2(connection, "msg_v2", now, 1000, """{"id":"new-model","providerID":"magpie"}""");

            // 另一种写法：扁平 modelID / providerID
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO session_message (id, session_id, type, seq, time_created, time_updated, data)
                VALUES ('msg_flat', 'ses_1', 'assistant', 2, $ts, $ts,
                        '{"role":"assistant","modelID":"flat-model","providerID":"prov","tokens":{"input":100,"output":10,"reasoning":5,"cache":{"read":100,"write":2}}}')
                """;
            command.Parameters.AddWithValue("$ts", now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        Assert.Equal(2, stats.Models.Count);
        Assert.Contains(stats.Models, m => m.ModelId == "new-model" && m.ProviderId == "magpie");
        Assert.Contains(stats.Models, m => m.ModelId == "flat-model" && m.ProviderId == "prov");
    }

    [Fact]
    public async Task NonAssistantAndTokenlessRows_AreIgnored()
    {
        var now = DateTimeOffset.Now;

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO session_message (id, session_id, type, seq, time_created, time_updated, data)
                VALUES
                  ('msg_user', 'ses_1', 'user', 1, $ts, $ts, '{"role":"user"}'),
                  ('msg_notokens', 'ses_1', 'assistant', 2, $ts, $ts, '{"role":"assistant"}')
                """;
            command.Parameters.AddWithValue("$ts", now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        Assert.Equal(0, stats.Steps);
        Assert.Equal(0, stats.Total.Total);
    }

    [Fact]
    public async Task CacheRatio_IsDerivedFromInputAndCacheRead()
    {
        var now = DateTimeOffset.Now;

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            InsertV2(connection, "msg_c", now, 300, """{"id":"m1","providerID":"p"}""");
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        // 100 / (100 + 300)
        Assert.Equal(0.25, stats.Total.CacheRatio, 4);
    }

    [Fact]
    public async Task EmptyDatabase_ReturnsZeroSeriesNotThrow()
    {
        var stats = await CreateProvider().FetchAsync(7, "day");

        Assert.Equal(7, stats.Series.Count);
        Assert.All(stats.Series, p => Assert.Equal(0, p.Total));
        Assert.Equal(0, stats.Steps);
        Assert.Empty(stats.Models);
    }

    [Fact]
    public async Task RowsWithZeroTokens_DoNotBreakAggregation()
    {
        var now = DateTimeOffset.Now;

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            InsertV2(connection, "msg_zero", now, 0, """{"id":"m1","providerID":"p"}""");
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        // 只有 10 输出 + 5 推理 + 100 缓存读 + 2 缓存写
        Assert.Equal(117, stats.Total.Total);
        Assert.Equal(1, stats.Steps);
    }
}