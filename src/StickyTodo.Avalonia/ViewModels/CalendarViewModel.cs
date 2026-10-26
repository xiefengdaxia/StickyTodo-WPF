using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Calendar;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.ViewModels;

/// <summary>
/// 日历看板：月视图 + 当日日程列表。
/// 数据来源是所有看板的待办（按目标日期投影，重复待办会展开成多次）。
/// </summary>
public partial class CalendarViewModel : ObservableObject, IAppearanceHost, IRefreshLocalized
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

    public bool HasSelectedEntries => SelectedDayEntries.Count > 0;

    public IReadOnlyList<string> WeekHeaders =>
    [
        Loc.T("cal.weekMon"), Loc.T("cal.weekTue"), Loc.T("cal.weekWed"), Loc.T("cal.weekThu"),
        Loc.T("cal.weekFri"), Loc.T("cal.weekSat"), Loc.T("cal.weekSun"),
    ];

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
        MonthTitle = Loc.F("cal.yearFormat", Year) + " " + Loc.F("cal.monthFormat", Month);

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
        StatusText = Loc.F("cal.itemCount", _monthEntries.Count(e => e.Entry.Day.Month == Month && e.Entry.Day.Year == Year));
    }

    private void UpdateSelectedDay()
    {
        var almanac = Almanac.Describe(SelectedDate);
        var lunar = almanac.Lunar;

        var week = WeekdayName(SelectedDate.DayOfWeek);

        // 说明：农历日期、干支、建除十二神、宜忌条目本身**保持中文**——
        // 这些是中文历法术语，硬译过去反而看不懂。只有标签跟随界面语言。
        var lunarWord = Loc.T("cal.lunarWord");
        var monthDay = Loc.F("cal.selectedDate", SelectedDate.Month, SelectedDate.Day);

        SelectedDateText = lunar.IsValid
            ? Loc.F("cal.selectedDateWithLunar", SelectedDate.Month, SelectedDate.Day, week, lunar.FullText)
            : $"{monthDay} · {week}";

        AlmanacDateText = lunar.IsValid
            ? $"{lunarWord}{lunar.FullText} · {LunarCalendar.YearGanZhi(lunar.Year)}年（{LunarCalendar.YearZodiac(lunar.Year)}）· {almanac.GanZhi}日 · {almanac.Officer}日"
            : $"{almanac.GanZhi}日 · {almanac.Officer}日";

        var extras = new List<string>();
        if (!string.IsNullOrEmpty(almanac.SolarTerm))
        {
            extras.Add(Loc.F("cal.solarTermTag", almanac.SolarTerm));
        }

        if (!string.IsNullOrEmpty(almanac.HolidayName))
        {
            extras.Add(Loc.F("cal.holidayTag", almanac.HolidayName));
        }

        if (almanac.Kind == DayKind.MakeupWorkday)
        {
            extras.Add(Loc.T("cal.makeupTag"));
        }

        AlmanacMetaText = string.Join(" · ", extras);

        AlmanacYiText = Loc.T("cal.yi") + " " + string.Join(" ", almanac.Yi);
        AlmanacJiText = Loc.T("cal.ji") + " " + string.Join(" ", almanac.Ji);
        ShowAlmanac = true;

        SelectedDayEntries.Clear();
        foreach (var entry in _monthEntries.Where(e => e.Entry.Day == SelectedDate))
        {
            SelectedDayEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasSelectedEntries));
    }

    private static string WeekdayName(DayOfWeek day) => Loc.T(day switch
    {
        DayOfWeek.Monday => "cal.weekdayMon",
        DayOfWeek.Tuesday => "cal.weekdayTue",
        DayOfWeek.Wednesday => "cal.weekdayWed",
        DayOfWeek.Thursday => "cal.weekdayThu",
        DayOfWeek.Friday => "cal.weekdayFri",
        DayOfWeek.Saturday => "cal.weekdaySat",
        _ => "cal.weekdaySun",
    });

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
    /// 切到别的月份时，把选中日期挪到该月内：有今天就用今天，否则用 1 号。
    /// 否则下方黄历会一直停在上个月。
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

    public async Task DeleteEntryAsync(CalendarEntryViewModel entry)
    {
        try
        {
            ErrorText = null;
            await Task.Run(() => _service.Delete(entry.TodoId));
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

    /// <summary>语言切换后重算所有由 C# 拼出来的文案。</summary>
    public void RefreshLocalizedText()
    {
        MonthTitle = Loc.F("cal.yearFormat", Year) + " " + Loc.F("cal.monthFormat", Month);
        StatusText = Loc.F("cal.itemCount", _monthEntries.Count(e => e.Entry.Day.Month == Month && e.Entry.Day.Year == Year));
        UpdateSelectedDay();
        OnPropertyChanged(nameof(WeekHeaders));
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
