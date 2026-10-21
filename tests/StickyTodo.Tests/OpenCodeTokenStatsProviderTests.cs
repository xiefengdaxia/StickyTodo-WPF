using Microsoft.Data.Sqlite;
using StickyTodo.Infrastructure.Token;

namespace StickyTodo.Tests;

/// <summary>
/// 本机 opencode 读库逻辑的测试。
/// 用临时库按真实表结构建最小数据集，重点验证三件容易错的事：
///   1. v1/v2 两套消息表不能重复计数；
///   2. 按小时/按天聚合必须限定在时间窗口内；
///   3. 模型取自 JSON 的 model.id / modelID 两种写法。
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
            $"{{\"role\":\"assistant\",\"modelID\":\"old-model\",\"providerID\":\"p\",\"tokens\":{{\"input\":{input},\"output\":10,\"reasoning\":5,\"cache\":{{\"read\":100,\"write\":2}}}}}}");
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
    public async Task SameIdInBothTables_IsCountedOnce()
    {
        var now = DateTimeOffset.Now;

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            // 同一个 id 在两套表里各一条 → 只算一次
            InsertV2(connection, "msg_same", now, 1000, """{"id":"m1","providerID":"p"}""");
            InsertV1(connection, "msg_same", now, 1000);
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        // 1000 输入 + 10 输出 + 5 推理 + 100 缓存读 + 2 缓存写
        Assert.Equal(1117, stats.Total.Total);
        Assert.Equal(1, stats.Steps);
    }

    [Fact]
    public async Task V1OnlyMessages_AreIncluded()
    {
        var now = DateTimeOffset.Now;

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            InsertV2(connection, "msg_a", now, 1000, """{"id":"m1","providerID":"p"}""");
            InsertV1(connection, "msg_legacy", now, 200); // v2 里没有 → 要补上
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        Assert.Equal(2, stats.Steps);
        Assert.Equal(1117 + 317, stats.Total.Total);
    }

    [Fact]
    public async Task HourlyBuckets_IgnoreOtherDays()
    {
        var today = DateTimeOffset.Now.Date;
        var yesterday = today.AddDays(-1);
        var todayMorning = new DateTimeOffset(today.AddHours(9));
        var yesterdayMorning = new DateTimeOffset(yesterday.AddHours(9));

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            InsertV2(connection, "msg_t", todayMorning, 500, """{"id":"m1","providerID":"p"}""");
            InsertV2(connection, "msg_y", yesterdayMorning, 999_999, """{"id":"m1","providerID":"p"}""");
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        Assert.Equal(24, stats.Series.Count);

        // 9 点只应包含今天的量，不能被昨天的同小时污染
        var nine = stats.Series[9];
        Assert.Equal(1, nine.Steps);
        Assert.Equal(500 + 10 + 5 + 100 + 2, nine.Total);

        // 其余时段为空
        Assert.All(stats.Series.Where(p => p.Label != "09:00"), p => Assert.Equal(0, p.Total));
    }

    [Fact]
    public async Task DailyBuckets_CoverWholeRangeAndIgnoreOutside()
    {
        var today = DateTimeOffset.Now.Date;

        // 全部用"已经过去"的时刻：今天 00:30、昨天 10:00；另塞一条 20 天前的
        var todayEarly = new DateTimeOffset(today.AddMinutes(30));
        var yesterdayTen = new DateTimeOffset(today.AddDays(-1).AddHours(10));

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

        // 每条消息 = input + 10 输出 + 5 推理 + 100 缓存读 + 2 缓存写
        // 昨天 100 → 217，今天 200 → 317；20 天前那条在窗口外，不能算进来
        Assert.Equal(217, stats.Series[1].Total);
        Assert.Equal(317, stats.Series[2].Total);
        Assert.Equal(534, stats.Series.Sum(p => p.Total));
        Assert.Equal(0, stats.Series[0].Total);
    }

    [Fact]
    public async Task Models_AreReadFromBothJsonShapes()
    {
        var now = DateTimeOffset.Now;

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            InsertV2(connection, "msg_v2", now, 1000, """{"id":"new-model","providerID":"magpie"}""");
            InsertV1(connection, "msg_v1", now, 100); // 扁平 modelID / providerID
        }

        var stats = await CreateProvider().FetchAsync(0, "hour");

        Assert.Equal(2, stats.Models.Count);
        Assert.Contains(stats.Models, m => m.ModelId == "new-model" && m.ProviderId == "magpie");
        Assert.Contains(stats.Models, m => m.ModelId == "old-model" && m.ProviderId == "p");
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
}