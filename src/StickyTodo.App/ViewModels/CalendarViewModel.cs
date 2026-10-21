using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.App.Services;
using StickyTodo.Core.Calendar;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Services;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 日历看板：月视图 + 当日日程列表。
/// 数据来源是所有看板的待办（按目标日期投影，重复待办会展开成多次）。
/// </summary>
public partial class CalendarViewModel : ObservableObject, IAppearanceHost
{
    private readonly TodoService _service;
    private readonly AppSettingsService _settings;
    private readonly SkinService _skin;
    private IReadOnlyList<CalendarEntryViewModel> _monthEntries = [];

    public CalendarViewModel(TodoService service, Board board, AppSettingsService settings, SkinService skin)
    {
        _service = service;
        _settings = settings;
        _skin = skin;
        Board = board;
        _boardName = board.Name;
        _isTopmost = settings.GetBoardTopmost(board.Id);
        _isLocked = settings.GetBoardLocked(board.Id);
        _windowOpacity = skin.WindowOpacity;

        var today = DateTime.Today;
        _year = today.Year;
        _month = today.Month;
        _selectedDate = DateOnly.FromDateTime(today);

        BuildYearOptions();
    }

    /// <summary>年月下拉框的候选：前 60 年到后 30 年。</summary>
    private void BuildYearOptions()
    {
        Years.Clear();
        var thisYear = DateTime.Today.Year;
        for (var year = thisYear - 60; year <= thisYear + 30; year++)
        {
            Years.Add(year);
        }
    }

    public ObservableCollection<int> Years { get; } = new();

    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();

    /// <summary>下拉框选年：直接跳到那一年同月。</summary>
    public async Task SelectYearAsync(int year)
    {
        if (year == Year || year < 1 || year > 9999)
        {
            return;
        }

        Year = year;
        ClampSelectedIntoMonth();
        await LoadAsync();
    }

    /// <summary>下拉框选月。</summary>
    public async Task SelectMonthAsync(int month)
    {
        if (month == Month || month is < 1 or > 12)
        {
            return;
        }

        Month = month;
        ClampSelectedIntoMonth();
        await LoadAsync();
    }

    public Board Board { get; }

    public string BoardId => Board.Id;

    public ObservableCollection<CalendarDayViewModel> Days { get; } = new();

    public ObservableCollection<CalendarEntryViewModel> SelectedDayEntries { get; } = new();

    public IReadOnlyList<string> WeekHeaders => ["一", "二", "三", "四", "五", "六", "日"];

    [ObservableProperty]
    private string _boardName;

    [ObservableProperty]
    private int _year;

    [ObservableProperty]
    private int _month;

    [ObservableProperty]
    private DateOnly _selectedDate;

    [ObservableProperty]
    private string _monthTitle = string.Empty;

    [ObservableProperty]
    private string _selectedDateText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>选中日期的黄历信息（宜忌等）。</summary>
    [ObservableProperty]
    private string _almanacDateText = string.Empty;

    [ObservableProperty]
    private string _almanacYiText = string.Empty;

    [ObservableProperty]
    private string _almanacJiText = string.Empty;

    [ObservableProperty]
    private string _almanacMetaText = string.Empty;

    [ObservableProperty]
    private bool _showAlmanac;

    [ObservableProperty]
    private bool _isTopmost;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private double _windowOpacity = 1.0;

    public async Task LoadAsync()
    {
        MonthTitle = $"{Year} 年 {Month} 月";

        var (fromDay, toDay) = MonthGrid.VisibleRange(Year, Month);
        var from = new DateTimeOffset(fromDay.ToDateTime(TimeOnly.MinValue));
        var to = new DateTimeOffset(toDay.ToDateTime(TimeOnly.MaxValue));

        var entries = await Task.Run(() => _service.GetCalendarEntries(from, to));
        _monthEntries = entries.Select(e => new CalendarEntryViewModel(e)).ToList();

        var byDay = _monthEntries
            .GroupBy(e => e.Entry.Day)
            .ToDictionary(g => g.Key, g => (IEnumerable<CalendarEntryViewModel>)g.ToList());

        Days.Clear();
        foreach (var cell in MonthGrid.Build(Year, Month))
        {
            var day = new CalendarDayViewModel(cell);
            day.SetEntries(byDay.TryGetValue(cell.Date, out var list) ? list : []);
            Days.Add(day);
        }

        UpdateSelectedDay();
        StatusText = $"本月 {_monthEntries.Count(e => e.Entry.Day.Month == Month && e.Entry.Day.Year == Year)} 项日程";
    }

    private void UpdateSelectedDay()
    {
        var almanac = Almanac.Describe(SelectedDate);
        var lunar = almanac.Lunar;

        var week = WeekdayName(SelectedDate.DayOfWeek);
        SelectedDateText = lunar.IsValid
            ? $"{SelectedDate.Month} 月 {SelectedDate.Day} 日 · {week} · 农历{lunar.FullText}"
            : $"{SelectedDate.Month} 月 {SelectedDate.Day} 日 · {week}";

        // 黄历：日期信息 + 建除十二神 + 宜/忌
        AlmanacDateText = lunar.IsValid
            ? $"农历{lunar.FullText} · {LunarCalendar.YearGanZhi(lunar.Year)}年（{LunarCalendar.YearZodiac(lunar.Year)}）· {almanac.GanZhi}日 · {almanac.Officer}日"
            : $"{almanac.GanZhi}日 · {almanac.Officer}日";

        var extras = new List<string>();
        if (!string.IsNullOrEmpty(almanac.SolarTerm))
        {
            extras.Add($"节气 {almanac.SolarTerm}");
        }

        if (!string.IsNullOrEmpty(almanac.HolidayName))
        {
            extras.Add($"节假日 {almanac.HolidayName}");
        }

        if (almanac.Kind == DayKind.MakeupWorkday)
        {
            extras.Add("调休补班");
        }

        AlmanacMetaText = string.Join(" · ", extras);

        AlmanacYiText = "宜 " + string.Join(" ", almanac.Yi);
        AlmanacJiText = "忌 " + string.Join(" ", almanac.Ji);
        ShowAlmanac = true;

        SelectedDayEntries.Clear();
        foreach (var entry in _monthEntries.Where(e => e.Entry.Day == SelectedDate))
        {
            SelectedDayEntries.Add(entry);
        }
    }

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    public async Task GoToMonthAsync(int delta)
    {
        (Year, Month) = MonthGrid.Shift(Year, Month, delta);
        ClampSelectedIntoMonth();
        await LoadAsync();
    }

    public async Task GoToTodayAsync()
    {
        var today = DateTime.Today;
        Year = today.Year;
        Month = today.Month;
        SelectedDate = DateOnly.FromDateTime(today);
        await LoadAsync();
    }

    /// <summary>
    /// 切到别的月份时，把选中日期挪到该月内：
    /// 有今天就用今天，否则用 1 号。否则下方黄历会一直停在上个月。
    /// </summary>
    private void ClampSelectedIntoMonth()
    {
        if (SelectedDate.Year == Year && SelectedDate.Month == Month)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        SelectedDate = today.Year == Year && today.Month == Month
            ? today
            : new DateOnly(Year, Month, 1);
    }

    public void SelectDay(CalendarDayViewModel day)
    {
        SelectedDate = day.Date;
        UpdateSelectedDay();
    }

    /// <summary>把某条待办改到新的日期（拖动到别的格子时用）。</summary>
    public async Task MoveEntryToDateAsync(CalendarEntryViewModel entry, DateOnly target)
    {
        try
        {
            ErrorText = null;

            // 保留原来的时刻，只换日期
            var time = entry.Entry.Occurrence.TimeOfDay;
            var due = new DateTimeOffset(target.ToDateTime(TimeOnly.FromTimeSpan(time)));
            await Task.Run(() => _service.SetDueAt(entry.TodoId, due));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public async Task ToggleEntryAsync(CalendarEntryViewModel entry, bool done)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() =>
            {
                if (done && entry.IsRecurring)
                {
                    _service.CompleteOccurrence(entry.TodoId);
                }
                else
                {
                    _service.SetDone(entry.TodoId, done);
                }
            });
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public void ToggleTopmost()
    {
        IsTopmost = !IsTopmost;
        _settings.SetBoardTopmost(BoardId, IsTopmost);
    }

    public void ToggleLock()
    {
        IsLocked = !IsLocked;
        _settings.SetBoardLocked(BoardId, IsLocked);
    }

    public async Task RenameBoardAsync(string name)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.RenameBoard(BoardId, name));
            Board.Name = name;
            BoardName = name;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    // ---------- 外观 ----------

    public string CurrentSkinKey => _skin.Current.Key;

    public double CurrentOpacity => _skin.Opacity;

    public double CurrentWindowOpacity => _skin.WindowOpacity;

    public void SetSkin(string skinKey)
    {
        _skin.SetSkin(skinKey);
        OnPropertyChanged(nameof(CurrentSkinKey));
    }

    public void SetOpacity(double opacity)
    {
        _skin.SetOpacity(opacity);
        OnPropertyChanged(nameof(CurrentOpacity));
    }

    public void SetWindowOpacity(double opacity)
    {
        _skin.SetWindowOpacity(opacity);
        WindowOpacity = _skin.WindowOpacity;
        OnPropertyChanged(nameof(CurrentWindowOpacity));
    }

    public void RefreshAppearance()
    {
        OnPropertyChanged(nameof(CurrentSkinKey));
        OnPropertyChanged(nameof(CurrentOpacity));
        OnPropertyChanged(nameof(CurrentWindowOpacity));
        WindowOpacity = _skin.WindowOpacity;
    }
}