using System.Windows.Threading;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Services;

/// <summary>
/// 本地提醒泵：每 20 秒查一次「到点且没提醒过」的待办，弹窗后立刻标记已提醒。
/// 查询/写库/防重入逻辑在 Core 的 ReminderPumpCore（与 Avalonia 版共用），这里只管定时。
/// </summary>
public sealed class ReminderPump : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly ReminderPumpCore _core;
    private readonly DispatcherTimer _timer;

    public ReminderPump(TodoService service)
    {
        _core = new ReminderPumpCore(service);
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += async (_, _) => await _core.CheckAsync();
    }

    /// <summary>到点的待办（在 UI 线程触发）。</summary>
    public event EventHandler<TodoItem>? ReminderDue
    {
        add => _core.ReminderDue += value;
        remove => _core.ReminderDue -= value;
    }

    public void Start()
    {
        _timer.Start();
        _ = _core.CheckAsync(); // 启动时也查一次，避免错过关机期间的提醒
    }

    /// <summary>手动查一轮（演示/回归用），与定时触发走同一条防重入路径。</summary>
    public Task CheckAsync() => _core.CheckAsync();

    public void Dispose() => _timer.Stop();
}
