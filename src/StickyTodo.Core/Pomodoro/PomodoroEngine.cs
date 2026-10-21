namespace StickyTodo.Core.Pomodoro;

/// <summary>番茄钟当前处在哪一段。</summary>
public enum PomodoroPhase
{
    /// <summary>专注。</summary>
    Focus = 0,

    /// <summary>短休息。</summary>
    ShortBreak = 1,

    /// <summary>长休息。</summary>
    LongBreak = 2,
}

/// <summary>番茄钟的运行状态。</summary>
public enum PomodoroStatus
{
    /// <summary>没有在跑。</summary>
    Idle = 0,

    /// <summary>正在倒计时。</summary>
    Running = 1,

    /// <summary>暂停。</summary>
    Paused = 2,
}

/// <summary>番茄钟的显示信息（给界面直接绑定）。</summary>
public sealed record PomodoroSnapshot(
    PomodoroPhase Phase,
    PomodoroStatus Status,
    TimeSpan Remaining,
    int RoundInCycle,
    int TotalRounds,
    int CompletedToday);

/// <summary>
/// 番茄钟状态机：只管"时间到了该切到哪一段"，不看界面也不自己起定时器。
/// 这样所有分支都能用单元测试覆盖，UI 只负责每秒推进一次。
/// </summary>
public sealed class PomodoroEngine
{
    private readonly TimeSpan _focus;
    private readonly TimeSpan _shortBreak;
    private readonly TimeSpan _longBreak;
    private readonly int _roundsPerCycle;

    private TimeSpan _remaining;

    public PomodoroEngine(
        TimeSpan? focus = null,
        TimeSpan? shortBreak = null,
        TimeSpan? longBreak = null,
        int roundsPerCycle = 4)
    {
        _focus = focus ?? TimeSpan.FromMinutes(25);
        _shortBreak = shortBreak ?? TimeSpan.FromMinutes(5);
        _longBreak = longBreak ?? TimeSpan.FromMinutes(15);
        _roundsPerCycle = Math.Max(2, roundsPerCycle);

        Phase = PomodoroPhase.Focus;
        Status = PomodoroStatus.Idle;
        _remaining = _focus;
    }

    public PomodoroPhase Phase { get; private set; }

    public PomodoroStatus Status { get; private set; }

    public TimeSpan Remaining => _remaining;

    /// <summary>本轮是这一串里的第几个（1..RoundsPerCycle）。</summary>
    public int RoundInCycle { get; private set; } = 1;

    public int RoundsPerCycle => _roundsPerCycle;

    /// <summary>今天完成了几段专注。</summary>
    public int CompletedToday { get; private set; }

    /// <summary>当前这一段的总时长。</summary>
    public TimeSpan CurrentLength => LengthOf(Phase);

    /// <summary>当前段的进度 0–1。</summary>
    public double Progress
    {
        get
        {
            var total = CurrentLength.TotalSeconds;
            return total <= 0 ? 0 : Math.Clamp(1 - (_remaining.TotalSeconds / total), 0, 1);
        }
    }

    /// <summary>刚刚走完一段，界面据此弹通知。读了就清空。</summary>
    public PomodoroPhase? JustFinished { get; private set; }

    public PomodoroPhase? TakeFinished()
    {
        var finished = JustFinished;
        JustFinished = null;
        return finished;
    }

    private TimeSpan LengthOf(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => _shortBreak,
        PomodoroPhase.LongBreak => _longBreak,
        _ => _focus,
    };

    /// <summary>开始 / 继续。</summary>
    public void Start()
    {
        if (Status == PomodoroStatus.Running)
        {
            return;
        }

        Status = PomodoroStatus.Running;
    }

    public void Pause()
    {
        if (Status == PomodoroStatus.Running)
        {
            Status = PomodoroStatus.Paused;
        }
    }

    public void Toggle()
    {
        if (Status == PomodoroStatus.Running)
        {
            Pause();
        }
        else
        {
            Start();
        }
    }

    /// <summary>重置当前这一段（不改阶段、不改第几个）。</summary>
    public void Reset()
    {
        Status = PomodoroStatus.Idle;
        _remaining = CurrentLength;
        JustFinished = null;
    }

    /// <summary>跳过当前段，直接进入下一段（不计入完成数）。</summary>
    public void Skip()
    {
        Advance(counted: false);
    }

    /// <summary>
    /// 推进 <paramref name="elapsed"/>。返回是否刚好在这一步走完。
    /// 只有 Running 才会走；暂停/空闲时原样返回 false。
    /// </summary>
    public bool Tick(TimeSpan elapsed)
    {
        if (Status != PomodoroStatus.Running || elapsed <= TimeSpan.Zero)
        {
            return false;
        }

        if (elapsed < _remaining)
        {
            _remaining -= elapsed;
            return false;
        }

        // 到点：把多出来的时间算进下一段，避免界面卡顿时丢失
        var overflow = elapsed - _remaining;
        _remaining = TimeSpan.Zero;
        Advance(counted: true);

        if (overflow > TimeSpan.Zero && Status == PomodoroStatus.Running)
        {
            Tick(overflow);
        }

        return true;
    }

    private void Advance(bool counted)
    {
        var finished = Phase;

        if (Phase == PomodoroPhase.Focus)
        {
            if (counted)
            {
                CompletedToday++;
            }

            var isLongBreak = RoundInCycle >= _roundsPerCycle;
            Phase = isLongBreak ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;

            // 长休息之后才回到第一个番茄
            if (isLongBreak)
            {
                RoundInCycle = 1;
            }
            else
            {
                RoundInCycle++;
            }

            if (counted)
            {
                JustFinished = finished;
            }
        }
        else
        {
            Phase = PomodoroPhase.Focus;
            _remaining = _focus;

            if (counted)
            {
                JustFinished = finished;
            }

            return;
        }

        _remaining = CurrentLength;
    }

    /// <summary>从设置里恢复"今天完成了几个"。</summary>
    public void RestoreCompletedCount(int count) => CompletedToday = Math.Max(0, count);

    /// <summary>把当前状态整理成一份快照。</summary>
    public PomodoroSnapshot Snapshot() => new(
        Phase,
        Status,
        _remaining,
        RoundInCycle,
        _roundsPerCycle,
        CompletedToday);

    /// <summary>把剩余时间格式化成 25:00 这样的文字。</summary>
    public static string Format(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var total = (int)Math.Ceiling(remaining.TotalSeconds);
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>阶段的中文名。</summary>
    public static string PhaseName(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => "短休息",
        PomodoroPhase.LongBreak => "长休息",
        _ => "专注",
    };
}