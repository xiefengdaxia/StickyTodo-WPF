using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

public sealed class ReminderAndMemoTests : DatabaseTestBase
{
    private TodoService CreateService() => new(Repository, new PermanentFreeEntitlement());

    [Fact]
    public void MemoBoard_StoresAndUpdatesContent()
    {
        var service = CreateService();
        var memo = service.CreateBoard("随手记", BoardKind.Memo);
        Assert.Equal(BoardKind.Memo, service.GetBoard(memo.Id)!.Kind);

        Assert.Null(service.GetNote(memo.Id));

        service.SaveNote(memo.Id, "第一版");
        Assert.Equal("第一版", service.GetNote(memo.Id)!.Content);

        service.SaveNote(memo.Id, "第二版\n换行也要保留");
        var note = service.GetNote(memo.Id)!;
        Assert.Equal("第二版\n换行也要保留", note.Content);
    }

    [Fact]
    public void DeletingMemoBoard_RemovesItsNote()
    {
        var service = CreateService();
        service.EnsureDefaultBoard();
        var memo = service.CreateBoard("临时便签", BoardKind.Memo);
        service.SaveNote(memo.Id, "内容");

        Assert.True(service.DeleteBoard(memo.Id));
        Assert.Null(service.GetNote(memo.Id));
    }

    [Fact]
    public void DueReminders_OnlyReturnsUnnotifiedUndone()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var now = DateTimeOffset.Now;

        var due = service.AddTodo(board.Id, "该响了");
        service.SetRemindAt(due.Id, now.AddMinutes(-1));

        var future = service.AddTodo(board.Id, "还没到");
        service.SetRemindAt(future.Id, now.AddHours(1));

        var done = service.AddTodo(board.Id, "已完成的不提醒");
        service.SetRemindAt(done.Id, now.AddMinutes(-5));
        service.SetDone(done.Id, true);

        var pending = service.GetDueReminders(now);
        Assert.Single(pending);
        Assert.Equal(due.Id, pending[0].Id);
    }

    [Fact]
    public void MarkReminded_StopsRepeatNotifications()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var item = service.AddTodo(board.Id, "响一次");
        service.SetRemindAt(item.Id, DateTimeOffset.Now.AddMinutes(-1));

        var first = Assert.Single(service.GetDueReminders(DateTimeOffset.Now));
        service.MarkReminded(first.Id, DateTimeOffset.Now);

        Assert.Empty(service.GetDueReminders(DateTimeOffset.Now));
    }

    [Fact]
    public void ChangingRemindTime_ResetsNotifiedFlag()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var item = service.AddTodo(board.Id, "改时间后要再响");
        service.SetRemindAt(item.Id, DateTimeOffset.Now.AddMinutes(-1));
        service.MarkReminded(item.Id, DateTimeOffset.Now);
        Assert.Empty(service.GetDueReminders(DateTimeOffset.Now));

        service.SetRemindAt(item.Id, DateTimeOffset.Now.AddMinutes(-1));
        Assert.Single(service.GetDueReminders(DateTimeOffset.Now));
    }

    [Fact]
    public void Snooze_PushesReminderIntoFuture()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var item = service.AddTodo(board.Id, "稍后提醒我");
        service.SetRemindAt(item.Id, DateTimeOffset.Now.AddMinutes(-1));
        Assert.Single(service.GetDueReminders(DateTimeOffset.Now));

        service.Snooze(item.Id, TimeSpan.FromMinutes(10));
        Assert.Empty(service.GetDueReminders(DateTimeOffset.Now));
    }

    [Fact]
    public void SetDueAt_RoundTrips()
    {
        var service = CreateService();
        var board = service.EnsureDefaultBoard();
        var item = service.AddTodo(board.Id, "有截止日");

        var due = DateTimeOffset.Now.Date.AddDays(3);
        service.SetDueAt(item.Id, due);

        var reloaded = Assert.Single(service.GetTodos(board.Id));
        Assert.Equal(due, reloaded.DueAt);

        service.SetDueAt(item.Id, null);
        Assert.Null(Assert.Single(service.GetTodos(board.Id)).DueAt);
    }
}

public sealed class DueDateTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void NoDueDate_ReturnsEmpty() => Assert.Equal(string.Empty, DueDateText.Describe(null, Now));

    [Theory]
    [InlineData(0, "今天到期")]
    [InlineData(1, "明天到期")]
    [InlineData(-1, "昨天到期")]
    [InlineData(3, "还剩 3 天")]
    [InlineData(-4, "已过期 4 天")]
    public void Describe_CoversAllCases(int dayOffset, string expected)
    {
        var due = Now.AddDays(dayOffset);
        Assert.Equal(expected, DueDateText.Describe(due, Now));
    }

    [Fact]
    public void IsOverdue_OnlyForPastDays()
    {
        Assert.False(DueDateText.IsOverdue(null, Now));
        Assert.False(DueDateText.IsOverdue(Now, Now));
        Assert.False(DueDateText.IsOverdue(Now.AddHours(-1), Now)); // 同一天不算过期
        Assert.True(DueDateText.IsOverdue(Now.AddDays(-1), Now));
    }
}