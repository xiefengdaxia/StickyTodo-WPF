using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

/// <summary>行内编辑：改标题的边界情况，以及草稿行新增。</summary>
public sealed class InlineEditTests : DatabaseTestBase
{
    private TodoService CreateService() => new(Repository, new PermanentFreeEntitlement());

    [Fact]
    public void UpdateTitle_TrimsAndPersists()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "原标题");

        service.UpdateTitle(todo.Id, "  新标题  ");

        Assert.Equal("新标题", service.GetTodos(board.Id).Single().Title);
    }

    [Fact]
    public void UpdateTitle_RejectsBlank()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "原标题");

        Assert.Throws<ArgumentException>(() => service.UpdateTitle(todo.Id, "   "));
        Assert.Equal("原标题", service.GetTodos(board.Id).Single().Title);
    }

    [Fact]
    public void UpdateTitle_UnknownId_Throws()
    {
        var service = CreateService();
        service.EnsureDefaultBoard();

        Assert.Throws<InvalidOperationException>(() => service.UpdateTitle("nope", "x"));
    }

    [Fact]
    public void UpdateTitle_KeepsOtherFields()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "原标题");
        service.SetDueAt(todo.Id, DateTimeOffset.Now.Date.AddDays(1));
        service.SetRemindAt(todo.Id, DateTimeOffset.Now.AddHours(2));
        service.SetRecurrence(todo.Id, "FREQ=DAILY");

        service.UpdateTitle(todo.Id, "改过的");

        var item = service.GetTodos(board.Id).Single();
        Assert.Equal("改过的", item.Title);
        Assert.NotNull(item.DueAt);
        Assert.NotNull(item.RemindAt);
        Assert.Equal("FREQ=DAILY", item.Recurrence);
    }

    [Fact]
    public void UpdateTitle_ClearThenReEdit_Works()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "A");

        service.UpdateTitle(todo.Id, "B");
        service.UpdateTitle(todo.Id, "C");

        Assert.Equal("C", service.GetTodos(board.Id).Single().Title);
    }

    [Fact]
    public void AddTodo_AppendsToEnd_LikeDraftRow()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();

        service.AddTodo(board.Id, "1");
        service.AddTodo(board.Id, "2");
        service.AddTodo(board.Id, "3");

        Assert.Equal(["1", "2", "3"], service.GetTodos(board.Id).Select(t => t.Title));
    }
}
