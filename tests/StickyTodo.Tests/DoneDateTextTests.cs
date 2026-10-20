using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

public sealed class DoneDateTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void NoDoneAt_ReturnsEmpty() => Assert.Equal(string.Empty, DoneDateText.Describe(null, Now));

    [Fact]
    public void Today_ShowsRelativeWord()
    {
        Assert.Equal("今天 09:05", DoneDateText.Describe(new DateTimeOffset(2026, 10, 20, 9, 5, 0, TimeSpan.FromHours(8)), Now));
    }

    [Fact]
    public void Yesterday_ShowsRelativeWord()
    {
        Assert.Equal("昨天 20:11", DoneDateText.Describe(new DateTimeOffset(2026, 10, 19, 20, 11, 0, TimeSpan.FromHours(8)), Now));
    }

    [Fact]
    public void SameYear_OmitsYear()
    {
        Assert.Equal("10月18日 08:00", DoneDateText.Describe(new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.FromHours(8)), Now));
    }

    [Fact]
    public void DifferentYear_IncludesYear()
    {
        Assert.Equal("2025年12月30日 08:00", DoneDateText.Describe(new DateTimeOffset(2025, 12, 30, 8, 0, 0, TimeSpan.FromHours(8)), Now));
    }

    [Fact]
    public void JustAfterMidnight_StillToday()
    {
        Assert.Equal("今天 00:01", DoneDateText.Describe(new DateTimeOffset(2026, 10, 20, 0, 1, 0, TimeSpan.FromHours(8)), Now));
    }
}

public sealed class TodoViewSortTests
{
    private sealed record Item(string Id, int Sort, DateTimeOffset? DoneAt);

    private static readonly DateTimeOffset Base = new(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void TodoView_KeepsManualSortOrder()
    {
        Item[] items =
        [
            new("c", 3, null),
            new("a", 1, null),
            new("b", 2, null),
        ];

        var result = TodoViewFilter.SortForView(items, TodoViewMode.Todo, i => i.Sort, i => i.DoneAt);
        Assert.Equal(["a", "b", "c"], result.Select(i => i.Id));
    }

    [Fact]
    public void DoneView_SortsByDoneTimeDescending()
    {
        Item[] items =
        [
            new("old", 1, Base.AddDays(-3)),
            new("newest", 3, Base),
            new("middle", 2, Base.AddDays(-1)),
        ];

        var result = TodoViewFilter.SortForView(items, TodoViewMode.Done, i => i.Sort, i => i.DoneAt);
        Assert.Equal(["newest", "middle", "old"], result.Select(i => i.Id));
    }

    [Fact]
    public void DoneView_NullDoneAt_GoesLast()
    {
        Item[] items =
        [
            new("no-date", 0, null),
            new("dated", 1, Base),
        ];

        var result = TodoViewFilter.SortForView(items, TodoViewMode.Done, i => i.Sort, i => i.DoneAt);
        Assert.Equal("dated", result[0].Id);
        Assert.Equal("no-date", result[1].Id);
    }
}