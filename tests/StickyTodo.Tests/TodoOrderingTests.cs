using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

public sealed class TodoOrderingTests
{
    [Fact]
    public void MoveBefore_MovesItemUp()
    {
        var result = TodoOrdering.MoveBefore(["a", "b", "c"], "c", "a");
        Assert.Equal(["c", "a", "b"], result);
    }

    [Fact]
    public void MoveBefore_MovesItemDown()
    {
        var result = TodoOrdering.MoveBefore(["a", "b", "c"], "a", "c");
        Assert.Equal(["b", "a", "c"], result);
    }

    [Fact]
    public void MoveBefore_NullTarget_GoesToEnd()
    {
        var result = TodoOrdering.MoveBefore(["a", "b", "c"], "a", null);
        Assert.Equal(["b", "c", "a"], result);
    }

    [Fact]
    public void MoveBefore_UnknownTarget_GoesToEnd()
    {
        var result = TodoOrdering.MoveBefore(["a", "b", "c"], "b", "not-exist");
        Assert.Equal(["a", "c", "b"], result);
    }

    [Fact]
    public void MoveBefore_SamePosition_KeepsOrder()
    {
        var result = TodoOrdering.MoveBefore(["a", "b", "c"], "b", "b");
        Assert.Equal(["a", "b", "c"], result);
    }

    [Fact]
    public void MoveBefore_UnknownMovingItem_KeepsOrderUntouched()
    {
        var result = TodoOrdering.MoveBefore(["a", "b", "c"], "zzz", "a");
        Assert.Equal(["a", "b", "c"], result);
    }
}