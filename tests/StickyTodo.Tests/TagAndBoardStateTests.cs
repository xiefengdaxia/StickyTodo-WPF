using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

public sealed class TagAndBoardStateTests : DatabaseTestBase
{
    private TodoService CreateService(IEntitlementService? entitlement = null) =>
        new(Repository, entitlement ?? new PermanentFreeEntitlement());

    [Fact]
    public void CreateTag_AssignsPaletteColor_AndRejectsDuplicates()
    {
        var service = CreateService();

        var first = service.CreateTag(" 工作 ");
        var second = service.CreateTag("生活");

        Assert.Equal("工作", first.Name);
        Assert.NotEqual(first.Color, second.Color);
        Assert.Equal(2, service.GetTags().Count);

        Assert.Throws<InvalidOperationException>(() => service.CreateTag("工作"));
        Assert.Throws<InvalidOperationException>(() => service.CreateTag("  工作  "));
    }

    [Fact]
    public void FreeTier_BlocksTagBeyondLimit()
    {
        var service = CreateService(new FreeTierEntitlement());

        service.CreateTag("t1");
        service.CreateTag("t2");
        service.CreateTag("t3");

        var ex = Assert.Throws<EntitlementLimitException>(() => service.CreateTag("t4"));
        Assert.Contains("最多支持 3 个标签", ex.Message);
    }

    [Fact]
    public void SetTodoTags_AndMap_RoundTrips()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var work = service.CreateTag("工作");
        var home = service.CreateTag("生活");

        var todo = service.AddTodo(board.Id, "写周报");
        service.SetTodoTags(todo.Id, [work.Id, home.Id]);

        var map = service.GetTodoTagMap(board.Id);
        Assert.True(map.ContainsKey(todo.Id));
        Assert.Equal(2, map[todo.Id].Count);

        service.SetTodoTags(todo.Id, [home.Id]);
        var updated = service.GetTodoTagMap(board.Id)[todo.Id];
        Assert.Single(updated);
        Assert.Equal(home.Id, updated[0]);
    }

    [Fact]
    public void DeleteTag_RemovesAssignments()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var tag = service.CreateTag("临时");
        var todo = service.AddTodo(board.Id, "x");
        service.SetTodoTags(todo.Id, [tag.Id]);

        Assert.True(service.DeleteTag(tag.Id));
        Assert.Empty(service.GetTags());
        Assert.Empty(Repository.GetTodoTagMap(board.Id));
    }

    [Fact]
    public void MoveBefore_PersistsNewOrder()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var a = service.AddTodo(board.Id, "A");
        var b = service.AddTodo(board.Id, "B");
        var c = service.AddTodo(board.Id, "C");

        service.MoveBefore(board.Id, c.Id, a.Id);

        var titles = service.GetTodos(board.Id).Select(t => t.Title).ToArray();
        Assert.Equal(["C", "A", "B"], titles);

        service.MoveBefore(board.Id, c.Id, null);
        titles = service.GetTodos(board.Id).Select(t => t.Title).ToArray();
        Assert.Equal(["A", "B", "C"], titles);
    }

    [Fact]
    public void BoardWindowState_RoundTrips_AndRejectsGarbage()
    {
        var service = CreateService();
        var board = service.CreateBoard("窗口测试");

        service.SaveBoardWindow(board.Id, new BoardWindowState(120, 80, 420, 560));
        var saved = service.GetBoard(board.Id)!.Window;
        Assert.NotNull(saved);
        Assert.Equal(120, saved!.Value.Left);
        Assert.Equal(80, saved.Value.Top);
        Assert.Equal(420, saved.Value.Width);
        Assert.Equal(560, saved.Value.Height);

        // 明显非法的尺寸不入库
        service.SaveBoardWindow(board.Id, new BoardWindowState(0, 0, double.NaN, 10));
        Assert.Equal(420, service.GetBoard(board.Id)!.Window!.Value.Width);
    }

    [Fact]
    public void DeleteBoard_RefusesToRemoveLastOne()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();

        Assert.Throws<InvalidOperationException>(() => service.DeleteBoard(board.Id));
    }

    [Fact]
    public void RenameBoard_TrimsAndPersists()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();

        Assert.True(service.RenameBoard(board.Id, "  今日待办  "));
        Assert.Equal("今日待办", service.GetBoard(board.Id)!.Name);
    }
}