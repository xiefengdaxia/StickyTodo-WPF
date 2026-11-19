using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;
using StickyTodo.Mcp;

namespace StickyTodo.Tests;

/// <summary>
/// 采集器（Jint 沙盒）回归：collect() 返回值、错误捕获、推送数据、网络失败不中断脚本。
/// 脚本行为（同步返回、异常、console.log）都覆盖；网络只在本地端口上测「连不上」这种确定路径。
/// </summary>
public sealed class CollectorHostTests : DatabaseTestBase
{
    private CollectorHost CreateCollector(TodoService todos) => new(todos);

    private Board CreateScriptedBoard(TodoService todos, string script)
    {
        var board = todos.CreateBoard("AI 面板", BoardKind.Custom);
        todos.SaveCustomBoard(board.Id, html: null, script: script, intervalSeconds: null);
        return board;
    }

    [Fact]
    public async Task RunNow_CollectsDataAndRaisesEvent()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        var board = CreateScriptedBoard(todos, """
            function collect() {
                console.log("采集中");
                return { title: "今日焦点", count: 3 };
            }
            """);
        var collector = CreateCollector(todos);
        collector.NotifyBoardsChanged();

        string? raised = null;
        collector.DataChanged += (_, id) => raised = id;

        var ran = await collector.RunNowAsync(board.Id);
        Assert.True(ran);

        var snapshot = collector.GetSnapshot(board.Id);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Error);
        Assert.NotNull(snapshot.DataJson);
        Assert.Contains("今日焦点", snapshot.DataJson);
        Assert.Contains("3", snapshot.DataJson);
        Assert.Contains("采集中", snapshot.LogTail);
        Assert.NotNull(snapshot.FetchedAt);
        Assert.Equal(board.Id, raised);
    }

    [Fact]
    public async Task RunNow_ScriptError_IsCapturedNotThrown()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        var board = CreateScriptedBoard(todos, "function collect() { throw new Error('目标挂了'); }");
        var collector = CreateCollector(todos);
        collector.NotifyBoardsChanged();

        var ran = await collector.RunNowAsync(board.Id);
        Assert.True(ran);

        var snapshot = collector.GetSnapshot(board.Id);
        Assert.NotNull(snapshot);
        Assert.Contains("目标挂了", snapshot!.Error);
    }

    [Fact]
    public async Task HttpFailure_ReturnsFailedResult_InsteadOfAbortingScript()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        var board = CreateScriptedBoard(todos, """
            function collect() {
                // 127.0.0.1:1 一定连不上：宿主必须把它折成 status=0 的结果，
                // 不能抛 .NET 异常（Jint 的 try/catch 接不住，整段脚本会中止）
                var res = http_get('http://127.0.0.1:1/', 1500);
                return { ok: res.ok, status: res.status, note: res.statusText };
            }
            """);
        var collector = CreateCollector(todos);
        collector.NotifyBoardsChanged();

        var ran = await collector.RunNowAsync(board.Id);
        Assert.True(ran);

        var snapshot = collector.GetSnapshot(board.Id);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Error);
        Assert.Contains("\"status\":0", snapshot.DataJson);
        Assert.Contains("\"ok\":false", snapshot.DataJson);
        // statusText 带着失败原因（TLS / 拒绝连接…），看板 Tooltip 用它
        Assert.DoesNotContain("\"note\":\"\"", snapshot.DataJson);
    }

    [Fact]
    public async Task RunNow_MissingCollectFunction_ReportsError()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        var board = CreateScriptedBoard(todos, "var x = 1; // 没定义 collect");
        var collector = CreateCollector(todos);
        collector.NotifyBoardsChanged();

        await collector.RunNowAsync(board.Id);

        var snapshot = collector.GetSnapshot(board.Id);
        Assert.NotNull(snapshot!.Error);
        Assert.Contains("collect", snapshot.Error);
    }

    [Fact]
    public void PushData_ValidJson_RaisesEventImmediately()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        var board = todos.CreateBoard("推数据", BoardKind.Custom);
        var collector = CreateCollector(todos);
        collector.NotifyBoardsChanged();

        string? raised = null;
        collector.DataChanged += (_, id) => raised = id;

        var ok = collector.PushData(board.Id, """{"hello":"世界"}""");
        Assert.True(ok);
        Assert.Equal(board.Id, raised);

        var snapshot = collector.GetSnapshot(board.Id);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Error);
        Assert.Contains("世界", snapshot.DataJson);
    }

    [Fact]
    public void PushData_InvalidJson_IsRejected()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        var board = todos.CreateBoard("推数据", BoardKind.Custom);
        var collector = CreateCollector(todos);
        collector.NotifyBoardsChanged();

        Assert.False(collector.PushData(board.Id, "{不是JSON"));
    }

    [Fact]
    public async Task RunNow_ScriptCanCallMcpTool_ThroughBridge()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        var board = CreateScriptedBoard(todos, """
            function collect() {
                var todos = mcp("list_todos", { include_done: true });
                return { count: todos.length, first: todos[0].title };
            }
            """);
        var collector = CreateCollector(todos);
        collector.NotifyBoardsChanged();

        string? calledTool = null;
        var payload = """[{"title":"买牛奶","isDone":false},{"title":"写周报","isDone":true}]""";
        collector.AttachMcpBridge((name, argsJson) =>
        {
            calledTool = name;
            Assert.Contains("include_done", argsJson);
            return payload;
        });

        var ran = await collector.RunNowAsync(board.Id);
        Assert.True(ran);

        var snapshot = collector.GetSnapshot(board.Id);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Error);
        Assert.Equal("list_todos", calledTool);
        Assert.Contains("买牛奶", snapshot.DataJson);
        Assert.Contains("2", snapshot.DataJson);
    }

    [Fact]
    public async Task RunNow_WithoutBridge_ScriptGetsClearError()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        var board = CreateScriptedBoard(todos, """function collect() { return mcp("list_todos", {}); }""");
        var collector = CreateCollector(todos);
        collector.NotifyBoardsChanged();

        var ran = await collector.RunNowAsync(board.Id);
        Assert.True(ran);

        var snapshot = collector.GetSnapshot(board.Id);
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Error);
        Assert.Contains("mcp()", snapshot.Error);
    }

    [Fact]
    public void Resync_RemovesCollectorOfDeletedBoard()
    {
        var todos = new TodoService(Repository, new PermanentFreeEntitlement());
        todos.EnsureDefaultBoard(); // 至少保留一个看板
        var board = CreateScriptedBoard(todos, "function collect() { return 1; }");
        var collector = CreateCollector(todos);
        collector.NotifyBoardsChanged();

        todos.DeleteBoard(board.Id);
        collector.NotifyBoardsChanged();

        Assert.Null(collector.GetSnapshot(board.Id));
    }
}
