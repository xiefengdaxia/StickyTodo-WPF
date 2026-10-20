using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

public sealed class TodoServiceTests : DatabaseTestBase
{
    [Fact]
    public void EnsureDefaultBoard_IsIdempotent()
    {
        var service = CreateService(new PermanentFreeEntitlement());

        var first = service.EnsureDefaultBoard();
        var second = service.EnsureDefaultBoard();

        Assert.Equal(first.Id, second.Id);
        Assert.True(first.IsDefault);
        Assert.Single(service.GetBoards());
    }

    [Fact]
    public void PermanentFree_HasNoBoardLimit()
    {
        var service = CreateService(new PermanentFreeEntitlement());

        for (var i = 0; i < 20; i++)
        {
            service.CreateBoard($"看板 {i}");
        }

        Assert.Equal(20, service.GetBoards().Count);
        Assert.Equal(PlanTier.Lifetime, service.Entitlement.Tier);
        Assert.True(service.Entitlement.IsPro);
        Assert.True(service.Entitlement.CanUseIcsCalendar);
        Assert.Equal(int.MaxValue, service.Entitlement.MaxTags);
    }

    [Fact]
    public void FreeTier_BlocksBoardBeyondLimit()
    {
        var service = CreateService(new FreeTierEntitlement());

        service.CreateBoard("看板 1");
        service.CreateBoard("看板 2");

        var ex = Assert.Throws<EntitlementLimitException>(() => service.CreateBoard("看板 3"));
        Assert.Contains("最多支持 2 个看板", ex.Message);
        Assert.Equal(2, service.GetBoards().Count);
    }

    [Fact]
    public void AddTodo_RejectsBlankTitle()
    {
        var service = CreateService(new PermanentFreeEntitlement());
        var board = service.EnsureDefaultBoard();

        Assert.Throws<ArgumentException>(() => service.AddTodo(board.Id, "   "));
    }

    [Fact]
    public void AddTodo_TrimsTitle_AndCanBeCompleted()
    {
        var service = CreateService(new PermanentFreeEntitlement());
        var board = service.EnsureDefaultBoard();

        var todo = service.AddTodo(board.Id, "  买牛奶  ");
        Assert.Equal("买牛奶", todo.Title);

        service.SetDone(todo.Id, true);
        var reloaded = Assert.Single(service.GetTodos(board.Id));
        Assert.True(reloaded.IsDone);
    }

    private TodoService CreateService(IEntitlementService entitlement) => new(Repository, entitlement);
}