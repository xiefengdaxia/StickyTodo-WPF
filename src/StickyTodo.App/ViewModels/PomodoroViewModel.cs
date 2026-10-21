using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Pomodoro;
using StickyTodo.Core.Services;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 番茄时钟：25 分钟专注 + 5 分钟短休息，每 4 个番茄换 15 分钟长休息。
/// 计时逻辑全在 Core 的 <see cref="PomodoroEngine"/>，这里只管把状态搬到界面。
/// </summary>
public partial class PomodoroViewModel : ObservableObject
{
    private readonly PomodoroEngine _engine;
    private readonly AppSettingsService _settings;

    private bool _sessionSaved;

    public PomodoroViewModel(AppSettingsService settings, PomodoroEngine engine)
    {
        _settings = settings;
        _engine = engine;
        _sessionSaved = settings.PomodoroCountDate == DateOnly.FromDateTime(DateTime.Today);
        if (_sessionSaved)
        {
            _engine.RestoreCompletedCount(settings.PomodoroCountToday);
        }

        Sync();
    }

    /// <summary>引擎本身，给回归测试用。</summary>
    public PomodoroEngine Engine => _engine;

    [ObservableProperty]
    private string _timeText = "25:00";

    [ObservableProperty]
    private string _phaseText = Loc.T("pomodoro.focus");

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _primaryActionText = Loc.T("pomodoro.start");

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _roundText = string.Empty;

    [ObservableProperty]
    private string _todayText = string.Empty;

    /// <summary>进度圆环的扫过角度（0–360），用 StrokeDashArray 不好控，直接算角度。</summary>
    [ObservableProperty]
    private double _progressAngle;

    /// <summary>专注 / 休息的配色区分。</summary>
    [ObservableProperty]
    private bool _isBreak;

    /// <summary>番茄钟阶段对应的翻译键。</summary>
    private static string PhaseKey(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => "pomodoro.shortBreak",
        PomodoroPhase.LongBreak => "pomodoro.longBreak",
        _ => "pomodoro.focus",
    };

    /// <summary>把引擎状态同步到界面。每秒调一次。</summary>
    public void Sync()
    {
        TimeText = PomodoroEngine.Format(_engine.Remaining);
        PhaseText = Loc.T(PhaseKey(_engine.Phase));
        IsRunning = _engine.Status == PomodoroStatus.Running;
        PrimaryActionText = IsRunning
            ? Loc.T("pomodoro.pause")
            : _engine.Remaining == _engine.CurrentLength ? Loc.T("pomodoro.start") : Loc.T("pomodoro.resume");
        Progress = _engine.Progress;
        ProgressAngle = _engine.Progress * 360;
        RoundText = Loc.F("pomodoro.round", _engine.RoundInCycle, _engine.RoundsPerCycle);
        TodayText = Loc.F("pomodoro.today", _engine.CompletedToday);
        IsBreak = _engine.Phase != PomodoroPhase.Focus;
    }

    /// <summary>推进时间。返回刚刚走完的阶段（没走完返回 null）。</summary>
    public PomodoroPhase? Advance(TimeSpan elapsed)
    {
        var finished = _engine.Tick(elapsed);
        Sync();

        if (!finished)
        {
            return null;
        }

        SaveCount();
        return _engine.TakeFinished();
    }

    [RelayCommand]
    public void Toggle()
    {
        _engine.Toggle();
        Sync();
    }

    [RelayCommand]
    public void Reset()
    {
        _engine.Reset();
        Sync();
    }

    [RelayCommand]
    public void Skip()
    {
        _engine.Skip();
        Sync();
    }

    /// <summary>把今天完成的个数写进设置（跨天自然清零）。</summary>
    private void SaveCount()
    {
        _settings.PomodoroCountToday = _engine.CompletedToday;
        _settings.PomodoroCountDate = DateOnly.FromDateTime(DateTime.Today);
        _sessionSaved = true;
    }

    /// <summary>窗口关闭前落盘一次。</summary>
    public void Flush() => SaveCount();
}