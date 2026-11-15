using System.Text.Json;
using System.Text.Json.Nodes;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Pomodoro;
using StickyTodo.Core.Services;
using StickyTodo.Infrastructure.Persistence;
using StickyTodo.Mcp;
using StickyTodo.Weather;

namespace StickyTodo.Tests;

/// <summary>
/// MCP 协议层回归：initialize/tools\/list/tools\/call 走真库（DatabaseTestBase），
/// 通知返回 202（null）、未知方法回 -32601。
/// </summary>
public sealed class McpProtocolHandlerTests : DatabaseTestBase
{
    private (McpProtocolHandler Handler, TodoService Todos, CollectorHost Collector) CreateHandler()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        var settings = new AppSettingsService(Repository);
        var collector = new CollectorHost(todos);
        var registry = new McpToolRegistry(
            todos,
            settings,
            new PomodoroEngine(),
            new WeatherService(),
            new FakeTokenProvider(),
            collector,
            new FakeMcpUiHost());
        return (new McpProtocolHandler(registry), todos, collector);
    }

    private static async Task<string> CallAsync(McpProtocolHandler handler, int id, string method, object? @params = null)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null)
        {
            message["params"] = JsonNode.Parse(JsonSerializer.Serialize(@params));
        }

        var response = await handler.HandleAsync(message.Deserialize<JsonElement>());
        Assert.NotNull(response);
        return response!;
    }

    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndCapabilities()
    {
        var (handler, _, _) = CreateHandler();

        var response = await CallAsync(handler, 1, "initialize", new { protocolVersion = "2025-06-18" });
        using var document = JsonDocument.Parse(response);

        Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("2025-06-18", document.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.True(document.RootElement.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("stickytodo", document.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ToolsCall_ListMarket_ReturnsBoards()
    {
        var (handler, _, _) = CreateHandler();

        var response = await CallAsync(handler, 10, "tools/call", new { name = "list_market", arguments = new { } });
        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");

        // 回归：JsonArray.Add(string) 走泛型 Add<T>，序列化选项缺 TypeInfoResolver 时会整包报错。
        var failed = result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;
        Assert.False(failed, result.GetProperty("content")[0].GetProperty("text").GetString());

        using var payload = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("directory").GetString()));
        Assert.True(payload.RootElement.GetProperty("boards").GetArrayLength() >= 1);
    }
    [Fact]
    public async Task ToolsList_ContainsBoardAndTodoTools()
    {
        var (handler, _, _) = CreateHandler();

        var response = await CallAsync(handler, 2, "tools/list");
        using var document = JsonDocument.Parse(response);

        var tools = document.RootElement.GetProperty("result").GetProperty("tools");
        Assert.True(tools.GetArrayLength() > 20);

        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        foreach (var expected in new[] { "list_boards", "open_board", "close_board", "create_board", "add_todo", "create_custom_board", "push_custom_data", "take_screenshot", "get_weather" })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public async Task ToolsCall_AddTodo_PersistsAndReturnsIt()
    {
        var (handler, todos, _) = CreateHandler();
        var board = todos.EnsureDefaultBoard();

        var response = await CallAsync(handler, 3, "tools/call", new
        {
            name = "add_todo",
            arguments = new { title = "MCP 加的待办", board_id = board.Id, due_at = "2026-01-02 09:30" },
        });

        Assert.Contains("result", response);
        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");
        if (result.TryGetProperty("isError", out var err))
        {
            Assert.False(err.GetBoolean());
        }

        Assert.Contains("MCP 加的待办", result.GetProperty("content")[0].GetProperty("text").GetString());

        var todos_ = todos.GetTodos(board.Id);
        Assert.Contains(todos_, t => t.Title == "MCP 加的待办");
        var item = todos_.First(t => t.Title == "MCP 加的待办");
        Assert.NotNull(item.DueAt);
        Assert.Equal(9, item.DueAt!.Value.Hour);
        Assert.Equal(30, item.DueAt!.Value.Minute);
    }

    [Fact]
    public async Task ToolsCall_CreateCustomBoard_PersistsScriptAndRunsCollector()
    {
        var (handler, todos, collector) = CreateHandler();

        var response = await CallAsync(handler, 4, "tools/call", new
        {
            name = "create_custom_board",
            arguments = new
            {
                name = "AI 面板",
                script = "function collect() { return { ok: true, n: 42 }; }",
                interval_seconds = 30,
                open = false,
            },
        });

        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");
        if (result.TryGetProperty("isError", out var err))
        {
            Assert.False(err.GetBoolean());
        }

        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"kind\":\"custom\"", text);

        var board = todos.GetBoards().Single(b => b.Kind == BoardKind.Custom);
        Assert.Equal("AI 面板", board.Name);
        Assert.Contains("collect", board.CustomScript);
        Assert.Equal(30, board.CustomIntervalSeconds);

        // create_custom_board 会立刻跑一次采集
        var snapshot = await WaitSnapshotAsync(collector, board.Id);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Error);
        Assert.Contains("42", snapshot.DataJson);
    }

    private static async Task<CollectorHost.CollectorSnapshot?> WaitSnapshotAsync(CollectorHost collector, string boardId)
    {
        for (var i = 0; i < 50; i++)
        {
            var snapshot = collector.GetSnapshot(boardId);
            if (snapshot?.DataJson is not null)
            {
                return snapshot;
            }

            await Task.Delay(100);
        }

        return collector.GetSnapshot(boardId);
    }

    [Fact]
    public async Task UnknownMethod_Returns32601()
    {
        var (handler, _, _) = CreateHandler();

        var response = await CallAsync(handler, 5, "resources/read");
        using var document = JsonDocument.Parse(response);

        Assert.Equal(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Notification_ReturnsNullFor202()
    {
        var (handler, _, _) = CreateHandler();

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
        };

        Assert.Null(await handler.HandleAsync(message.Deserialize<JsonElement>()));
    }

    [Fact]
    public void CustomBoardKind_RoundTripsThroughStorage()
    {
        Assert.Equal("custom", BoardKinds.ToStorage(BoardKind.Custom));
        Assert.Equal(BoardKind.Custom, BoardKinds.Parse("custom"));
        Assert.Equal("自定义看板", BoardKinds.DisplayName(BoardKind.Custom));
        Assert.False(BoardKinds.IsToolBoard(BoardKind.Custom));
    }

    private sealed class FakeTokenProvider : ITokenStatsProvider
    {
        public string SourceDescription => "测试数据源";
        public bool IsAvailable => false;

        public Task<TokenStats> FetchAsync(int days, string granularity, CancellationToken cancellationToken = default) =>
            Task.FromResult(TokenStats.Empty);
    }

    private sealed class FakeMcpUiHost : IMcpUiHost
    {
        public T RunOnUi<T>(Func<T> action) => action();

        public void RunOnUi(Action action) => action();

        public bool OpenBoard(string boardId) => true;

        public bool CloseBoard(string boardId) => true;

        public IReadOnlyList<string> OpenBoardIds { get; } = Array.Empty<string>();

        public Task RefreshOpenBoardsAsync() => Task.CompletedTask;

        public void ReloadCustomBoard(string boardId)
        {
        }

        public void ApplyEyeSettings()
        {
        }

        public void Notify(string title, string message)
        {
        }

        public Task<byte[]?> CaptureScreenshotAsync(CancellationToken cancellation = default) =>
            Task.FromResult<byte[]?>(null);
    }
}
