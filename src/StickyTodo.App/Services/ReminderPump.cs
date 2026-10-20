using System.Windows.Threading;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Services;

/// <summary>
/// 本地提醒泵：每 20 秒查一次「到点且没提醒过」的待办，弹窗后立刻标记已提醒。
/// 全程在本机完成，不依赖任何推送服务。
/// </summary>
public sealed class ReminderPump : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly TodoService _service;
    private readonly DispatcherTimer _timer;

    public ReminderPump(TodoService service)
    {
        _service = service;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    /// <summary>到点的待办（在 UI 线程触发）。</summary>
    public event EventHandler<TodoItem>? ReminderDue;

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync(); // 启动时也查一次，避免错过关机期间的提醒
    }

    public async Task CheckAsync()
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
                _service.MarkReminded(item.Id, DateTimeOffset.Now);
            }
            catch (Exception)
            {
                continue;
            }

            ReminderDue?.Invoke(this, item);
        }
    }

    public void Dispose() => _timer.Stop();
}