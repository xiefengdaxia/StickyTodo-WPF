using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

/// <summary>
/// 提醒泵核心逻辑（从 WPF/Avalonia 抽出的公共部分）：
/// 到点触发事件、先标记后发事件、事件在 await 之后回到调用方上下文。
/// </summary>
public sealed class ReminderPumpCoreTests : DatabaseTestBase
{
    [Fact]
    public async Task CheckAsync_FiresEvent_AndMarksReminded()
    {
        var service = new TodoService(Repository, new PermanentFreeEntitlement());
        var board = service.EnsureDefaultBoard();
        var todo = service.AddTodo(board.Id, "到点的待办");
        service.SetRemindAt(todo.Id, DateTimeOffset.Now.AddSeconds(-1));

        var pump = new ReminderPumpCore(service);
        var fired = new List<TodoItem>();
        pump.ReminderDue += (_, item) => fired.Add(item);

        await pump.CheckAsync();

        var item = Assert.Single(fired);
        Assert.Equal(todo.Id, item.Id);

        // 标记过后不再重复弹
        await pump.CheckAsync();
        Assert.Single(fired);
        Assert.Empty(service.GetDueReminders(DateTimeOffset.Now));
    }

    [Fact]
    public async Task CheckAsync_DatabaseFailure_IsSwallowed()
    {
        // 指向一个无法创建库的路径（父目录不存在），GetDueReminders 会抛 SQLite 异常
        var badRepository = new StickyTodo.Infrastructure.Persistence.SqliteTodoRepository(
            Path.Combine(Path.GetTempPath(), $"stickytodo-missing-{Guid.NewGuid():N}", "x.db"));
        var service = new TodoService(badRepository, new PermanentFreeEntitlement());

        var pump = new ReminderPumpCore(service);
        var fired = 0;
        pump.ReminderDue += (_, _) => fired++;

        // 库临时不可用不该抛出、也不该弹提醒，留给下一轮再试
        await pump.CheckAsync();

        Assert.Equal(0, fired);
    }
}
