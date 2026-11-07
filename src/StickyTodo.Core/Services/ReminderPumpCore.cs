using StickyTodo.Core.Entities;

namespace StickyTodo.Core.Services;

/// <summary>
/// 本地提醒泵的核心逻辑：每轮查「到点且没提醒过」的待办，标记已提醒后发事件。
/// 与 UI 框架无关——WPF / Avalonia 各用自家定时器驱动 <see cref="CheckAsync"/>，逻辑只此一份。
/// 全程在本机完成，不依赖任何推送服务。
/// </summary>
public sealed class ReminderPumpCore
{
    private readonly TodoService _service;

    public ReminderPumpCore(TodoService service)
    {
        _service = service;
    }

    /// <summary>到点的待办（回调发生在调用方的同步上下文上，通常是 UI 线程）。</summary>
    public event EventHandler<TodoItem>? ReminderDue;

    /// <summary>正在查询（防止上一轮还没结束、这一轮又进来，导致同一条提醒弹两次）。</summary>
    private bool _checking;

    public async Task CheckAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;

        try
        {
            IReadOnlyList<TodoItem> due;
            try
            {
                due = await Task.Run(() => _service.GetDueReminders(DateTimeOffset.Now));
            }
            catch (Exception)
            {
                // 数据库临时不可用不该影响主流程，下一轮再试
                return;
            }

            foreach (var item in due)
            {
                // 先标记再弹窗：即使弹窗过程出错也不会反复骚扰
                try
                {
                    // 写库放到后台，别在 UI 线程上做一次磁盘往返
                    var id = item.Id;
                    await Task.Run(() => _service.MarkReminded(id, DateTimeOffset.Now));
                }
                catch (Exception)
                {
                    continue;
                }

                ReminderDue?.Invoke(this, item);
            }
        }
        finally
        {
            _checking = false;
        }
    }
}
