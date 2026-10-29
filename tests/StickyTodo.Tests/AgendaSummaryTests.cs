using StickyTodo.Core.Calendar;
using StickyTodo.Core.Entities;

namespace StickyTodo.Tests;

/// <summary>
/// 桌面时钟的「今日日程」摘要：总数 / 完成数 / 下一项。
/// 这里的边界最容易出错（时间都过了、全部完成、跨天条目混进来），所以单独覆盖。
/// </summary>
public sealed class AgendaSummaryTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static CalendarEntry Entry(string title, int hour, int minute, bool done = false, DateOnly? day = null)
    {
        var occurrence = new DateTimeOffset((day ?? Today).ToDateTime(new TimeOnly(hour, minute)));
        var todo = new TodoItem
        {
            Id = Guid.NewGuid().ToString("n"),
            BoardId = "b1",
            Title = title,
            IsDone = done,
            DueAt = occurrence,
            CreatedAt = occurrence,
            UpdatedAt = occurrence,
        };

        return new CalendarEntry(todo, occurrence, IsVirtual: false);
    }

    [Fact]
    public void EmptyDay_IsEmpty_AndHasNoNext()
    {
        var snapshot = AgendaSummary.Build([], Today, new DateTimeOffset(Today.ToDateTime(new TimeOnly(9, 0))));

        Assert.True(snapshot.IsEmpty);
        Assert.Equal(0, snapshot.Total);
        Assert.Equal(0, snapshot.Done);
        Assert.Null(snapshot.Next);
    }

    [Fact]
    public void Next_IsTheEarliestUpcoming()
    {
        var now = new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)));
        var snapshot = AgendaSummary.Build(
        [
            Entry("早会", 9, 0),
            Entry("交周报", 14, 0),
            Entry("复盘", 18, 0),
        ], Today, now);

        Assert.Equal(3, snapshot.Total);
        Assert.Equal(0, snapshot.Done);
        Assert.Equal("交周报", snapshot.Next!.Todo.Title);
    }

    [Fact]
    public void WhenAllTimesPassed_FallsBackToEarliestPending()
    {
        var now = new DateTimeOffset(Today.ToDateTime(new TimeOnly(23, 0)));
        var snapshot = AgendaSummary.Build(
        [
            Entry("早会", 9, 0),
            Entry("交周报", 14, 0),
        ], Today, now);

        Assert.Equal("早会", snapshot.Next!.Todo.Title);
    }

    [Fact]
    public void DoneItems_AreCounted_ButNeverChosenAsNext()
    {
        var now = new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)));
        var snapshot = AgendaSummary.Build(
        [
            Entry("早会", 9, 0, done: true),
            Entry("交周报", 14, 0),
        ], Today, now);

        Assert.Equal(2, snapshot.Total);
        Assert.Equal(1, snapshot.Done);
        Assert.Equal(1, snapshot.Pending);
        Assert.Equal("交周报", snapshot.Next!.Todo.Title);
    }

    [Fact]
    public void AllDone_ReportsAllDone_AndHasNoNext()
    {
        var now = new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)));
        var snapshot = AgendaSummary.Build(
        [
            Entry("早会", 9, 0, done: true),
            Entry("交周报", 14, 0, done: true),
        ], Today, now);

        Assert.True(snapshot.AllDone);
        Assert.Equal(2, snapshot.Total);
        Assert.Equal(0, snapshot.Pending);
        Assert.Null(snapshot.Next);
    }

    [Fact]
    public void EntriesFromOtherDays_AreIgnored()
    {
        var now = new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)));
        var snapshot = AgendaSummary.Build(
        [
            Entry("昨天的事", 9, 0, day: Today.AddDays(-1)),
            Entry("今天的事", 14, 0),
            Entry("明天的事", 9, 0, day: Today.AddDays(1)),
        ], Today, now);

        Assert.Equal(1, snapshot.Total);
        Assert.Equal("今天的事", snapshot.Next!.Todo.Title);
    }
}
