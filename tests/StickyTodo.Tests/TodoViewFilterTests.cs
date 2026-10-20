using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

public sealed class TodoViewFilterTests
{
    private sealed record Item(string Id, bool IsDone);

    private static readonly Item[] Items =
    [
        new("a", false),
        new("b", true),
        new("c", false),
        new("d", true),
    ];

    [Fact]
    public void TodoView_KeepsOnlyUndone()
    {
        var result = TodoViewFilter.Filter(Items, TodoViewMode.Todo, i => i.IsDone);
        Assert.Equal(["a", "c"], result.Select(i => i.Id));
    }

    [Fact]
    public void DoneView_KeepsOnlyDone()
    {
        var result = TodoViewFilter.Filter(Items, TodoViewMode.Done, i => i.IsDone);
        Assert.Equal(["b", "d"], result.Select(i => i.Id));
    }

    [Theory]
    [InlineData(false, TodoViewMode.Todo, true)]
    [InlineData(true, TodoViewMode.Todo, false)]
    [InlineData(false, TodoViewMode.Done, false)]
    [InlineData(true, TodoViewMode.Done, true)]
    public void Matches_IsMutuallyExclusive(bool isDone, TodoViewMode mode, bool expected)
    {
        Assert.Equal(expected, TodoViewFilter.Matches(isDone, mode));
    }

    [Fact]
    public void EmptyList_StaysEmpty()
    {
        Assert.Empty(TodoViewFilter.Filter(Array.Empty<Item>(), TodoViewMode.Todo, i => i.IsDone));
        Assert.Empty(TodoViewFilter.Filter(Array.Empty<Item>(), TodoViewMode.Done, i => i.IsDone));
    }

    [Fact]
    public void DisplayAndHint_DifferPerMode()
    {
        Assert.Equal("待办", TodoViewFilter.DisplayName(TodoViewMode.Todo));
        Assert.Equal("已完成", TodoViewFilter.DisplayName(TodoViewMode.Done));
        Assert.NotEqual(
            TodoViewFilter.EmptyHint(TodoViewMode.Todo),
            TodoViewFilter.EmptyHint(TodoViewMode.Done));
    }
}