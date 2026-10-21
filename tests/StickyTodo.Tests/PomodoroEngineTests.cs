using StickyTodo.Core.Pomodoro;

namespace StickyTodo.Tests;

/// <summary>番茄钟状态机测试：所有分支都走一遍，不涉及界面。</summary>
public sealed class PomodoroEngineTests
{
    private static PomodoroEngine CreateEngine() => new(
        focus: TimeSpan.FromMinutes(25),
        shortBreak: TimeSpan.FromMinutes(5),
        longBreak: TimeSpan.FromMinutes(15),
        roundsPerCycle: 4);

    [Fact]
    public void Initial_IsIdleFocus25()
    {
        var engine = CreateEngine();

        Assert.Equal(PomodoroPhase.Focus, engine.Phase);
        Assert.Equal(PomodoroStatus.Idle, engine.Status);
        Assert.Equal(TimeSpan.FromMinutes(25), engine.Remaining);
        Assert.Equal(1, engine.RoundInCycle);
        Assert.Equal(4, engine.RoundsPerCycle);
        Assert.Equal(0, engine.CompletedToday);
    }

    [Fact]
    public void Tick_DoesNothingWhenIdle()
    {
        var engine = CreateEngine();

        Assert.False(engine.Tick(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromMinutes(25), engine.Remaining);
    }

    [Fact]
    public void Tick_CountsDownWhenRunning()
    {
        var engine = CreateEngine();
        engine.Start();

        Assert.False(engine.Tick(TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromMinutes(24), engine.Remaining);
        Assert.Equal(1d / 25d, engine.Progress, 4);
    }

    [Fact]
    public void Pause_StopsCountdown()
    {
        var engine = CreateEngine();
        engine.Start();
        engine.Tick(TimeSpan.FromMinutes(5));
        engine.Pause();

        Assert.False(engine.Tick(TimeSpan.FromMinutes(10)));
        Assert.Equal(TimeSpan.FromMinutes(20), engine.Remaining);
        Assert.Equal(PomodoroStatus.Paused, engine.Status);
    }

    [Fact]
    public void Toggle_FlipsBetweenRunningAndPaused()
    {
        var engine = CreateEngine();

        engine.Toggle();
        Assert.Equal(PomodoroStatus.Running, engine.Status);

        engine.Toggle();
        Assert.Equal(PomodoroStatus.Paused, engine.Status);
    }

    [Fact]
    public void FocusCompletion_MovesToShortBreakAndCounts()
    {
        var engine = CreateEngine();
        engine.Start();

        var finished = engine.Tick(TimeSpan.FromMinutes(25));

        Assert.True(finished);
        Assert.Equal(PomodoroPhase.ShortBreak, engine.Phase);
        Assert.Equal(TimeSpan.FromMinutes(5), engine.Remaining);
        Assert.Equal(1, engine.CompletedToday);
        Assert.Equal(2, engine.RoundInCycle);
        Assert.Equal(PomodoroPhase.Focus, engine.TakeFinished());
    }

    [Fact]
    public void TakeFinished_ClearsAfterReading()
    {
        var engine = CreateEngine();
        engine.Start();
        engine.Tick(TimeSpan.FromMinutes(25));

        Assert.NotNull(engine.TakeFinished());
        Assert.Null(engine.TakeFinished());
    }

    [Fact]
    public void FourthFocus_GivesLongBreak()
    {
        var engine = CreateEngine();
        engine.Start();

        // 走完 3 个完整番茄（专注 + 短休息）
        for (var round = 1; round <= 3; round++)
        {
            engine.Tick(TimeSpan.FromMinutes(25));
            Assert.Equal(PomodoroPhase.ShortBreak, engine.Phase);
            engine.Tick(TimeSpan.FromMinutes(5));
            Assert.Equal(PomodoroPhase.Focus, engine.Phase);
            Assert.Equal(round + 1, engine.RoundInCycle);
        }

        engine.Tick(TimeSpan.FromMinutes(25));

        Assert.Equal(PomodoroPhase.LongBreak, engine.Phase);
        Assert.Equal(TimeSpan.FromMinutes(15), engine.Remaining);
        Assert.Equal(4, engine.CompletedToday);
        Assert.Equal(1, engine.RoundInCycle);
    }

    [Fact]
    public void LongBreakReturn_GoesBackToFocus()
    {
        var engine = CreateEngine();
        engine.Start();

        for (var round = 1; round <= 4; round++)
        {
            engine.Tick(TimeSpan.FromMinutes(25));
            engine.Tick(engine.Remaining);
        }

        Assert.Equal(PomodoroPhase.Focus, engine.Phase);
        Assert.Equal(TimeSpan.FromMinutes(25), engine.Remaining);
        Assert.Equal(4, engine.CompletedToday);
    }

    [Fact]
    public void Overflow_CarriesIntoNextPhase()
    {
        var engine = new PomodoroEngine(focus: TimeSpan.FromSeconds(10), shortBreak: TimeSpan.FromSeconds(5));
        engine.Start();

        var finished = engine.Tick(TimeSpan.FromSeconds(12));

        Assert.True(finished);
        Assert.Equal(PomodoroPhase.ShortBreak, engine.Phase);
        Assert.Equal(TimeSpan.FromSeconds(3), engine.Remaining);
    }

    [Fact]
    public void Skip_DoesNotCountAsCompleted()
    {
        var engine = CreateEngine();
        engine.Start();

        engine.Skip();

        Assert.Equal(PomodoroPhase.ShortBreak, engine.Phase);
        Assert.Equal(0, engine.CompletedToday);
        Assert.Null(engine.TakeFinished());
    }

    [Fact]
    public void Reset_RestoresCurrentPhaseLength()
    {
        var engine = CreateEngine();
        engine.Start();
        engine.Tick(TimeSpan.FromMinutes(10));

        engine.Reset();

        Assert.Equal(TimeSpan.FromMinutes(25), engine.Remaining);
        Assert.Equal(PomodoroStatus.Idle, engine.Status);
        Assert.Equal(PomodoroPhase.Focus, engine.Phase);
    }

    [Fact]
    public void RestoreCompletedCount_ClampsNegative()
    {
        var engine = CreateEngine();

        engine.RestoreCompletedCount(-5);
        Assert.Equal(0, engine.CompletedToday);

        engine.RestoreCompletedCount(7);
        Assert.Equal(7, engine.CompletedToday);
    }

    [Fact]
    public void Progress_IsMonotonic()
    {
        var engine = CreateEngine();
        engine.Start();

        var last = 0d;
        // 走 24 分钟即可（第 25 分钟会切到休息段，进度自然归零）
        for (var i = 0; i < 24; i++)
        {
            engine.Tick(TimeSpan.FromMinutes(1));
            Assert.True(engine.Progress >= last);
            last = engine.Progress;
        }
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(60, "01:00")]
    [InlineData(25 * 60, "25:00")]
    [InlineData(1499, "24:59")]
    public void Format_PadsCorrectly(int seconds, string expected)
    {
        Assert.Equal(expected, PomodoroEngine.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Format_RoundsUpPartialSeconds()
    {
        // 剩 0.4 秒时不该显示 00:00，否则最后一下会"跳"
        Assert.Equal("00:01", PomodoroEngine.Format(TimeSpan.FromMilliseconds(400)));
    }

    [Theory]
    [InlineData(PomodoroPhase.Focus, "专注")]
    [InlineData(PomodoroPhase.ShortBreak, "短休息")]
    [InlineData(PomodoroPhase.LongBreak, "长休息")]
    public void PhaseName_IsChinese(PomodoroPhase phase, string expected)
    {
        Assert.Equal(expected, PomodoroEngine.PhaseName(phase));
    }

    [Fact]
    public void Snapshot_ReflectsState()
    {
        var engine = CreateEngine();
        engine.Start();
        engine.Tick(TimeSpan.FromMinutes(5));

        var snapshot = engine.Snapshot();

        Assert.Equal(PomodoroPhase.Focus, snapshot.Phase);
        Assert.Equal(PomodoroStatus.Running, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(20), snapshot.Remaining);
        Assert.Equal(1, snapshot.RoundInCycle);
        Assert.Equal(4, snapshot.TotalRounds);
    }
}