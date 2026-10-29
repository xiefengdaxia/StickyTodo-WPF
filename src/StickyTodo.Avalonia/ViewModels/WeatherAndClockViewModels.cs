using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyTodo.Core.Calendar;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Services;
using StickyTodo.Notes.Services;
using StickyTodo.Weather;

namespace StickyTodo.Notes.ViewModels;

/// <summary>天气看板的一天。</summary>
public sealed class WeatherDayRow
{
    public WeatherDayRow(WeatherDayInfo info)
    {
        DayLabel = info.DayLabel;
        DateLabel = info.DateLabel;
        Text = info.Text;
        TempMaxText = $"{info.TempMax:0}°C";
        TempMinText = $"{info.TempMin:0}°C";
        Wind = info.Wind;
        IconGlyph = WeatherGlyph.For(info.Icon);
        IconGlyph2 = WeatherGlyph.For(info.Icon);
    }

    public string DayLabel { get; }

    public string DateLabel { get; }

    public string Text { get; }

    public string TempMaxText { get; }

    public string TempMinText { get; }

    public string Wind { get; }

    /// <summary>第一行图标（文字符号，任何 DPI 清晰）。</summary>
    public string IconGlyph { get; }

    /// <summary>第二行图标（与第一行同一天气）。</summary>
    public string IconGlyph2 { get; }
}

/// <summary>天气现象 → 文字符号。</summary>
public static class WeatherGlyph
{
    public static string For(string icon) => icon switch
    {
        "sunny" => "☀",
        "mostly-sunny" => "🌤",
        "partly-cloudy" => "⛅",
        "cloudy" => "☁",
        "fog" => "🌫",
        "drizzle" => "🌦",
        "rain-light" => "🌧",
        "rain" => "🌧",
        "rain-heavy" => "⛈",
        "showers" => "🌦",
        "sleet" => "🌨",
        "snow" or "snow-light" or "snow-heavy" => "❄",
        "thunder" => "⛈",
        _ => "・",
    };
}

/// <summary>城市胶囊。</summary>
public sealed partial class WeatherCityRow : ObservableObject
{
    public WeatherCityRow(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>是不是默认城市（桌面时钟优先显示它）。</summary>
    [ObservableProperty]
    private bool _isDefault;

    /// <summary>胶囊上的文字：默认城市带个 ★。</summary>
    public string DisplayName => IsDefault ? Name + " ★" : Name;

    partial void OnIsDefaultChanged(bool value) => OnPropertyChanged(nameof(DisplayName));
}

/// <summary>天气看板。</summary>
public sealed partial class WeatherViewModel : ObservableObject
{
    /// <summary>城市数量上限（再多加界面就排不下了）。</summary>
    private const int MaxCities = 12;

    private readonly WeatherService _weather;
    private readonly AppSettingsService _settings;

    /// <summary>最近一次成功取到的报告（来源说明要据此判断是否按市查询）。</summary>
    private WeatherReport? _report;

    public WeatherViewModel(WeatherService weather, AppSettingsService settings)
    {
        _weather = weather;
        _settings = settings;

        foreach (var city in settings.WeatherCities)
        {
            Cities.Add(new WeatherCityRow(city));
        }

        RefreshDefaultFlags();
        SelectedCity = Cities.FirstOrDefault(c => c.IsDefault) ?? Cities.FirstOrDefault();
    }

    /// <summary>把"默认"标记刷到城市胶囊上（★）。</summary>
    private void RefreshDefaultFlags()
    {
        var defaultCity = _settings.EffectiveWeatherDefaultCity;

        foreach (var city in Cities)
        {
            city.IsDefault = string.Equals(city.Name, defaultCity, StringComparison.Ordinal);
        }
    }

    /// <summary>把当前城市设为默认（桌面时钟优先显示它，下次打开也选中它）。</summary>
    public bool SetDefaultCity()
    {
        var current = SelectedCity;
        if (current is null)
        {
            return false;
        }

        _settings.WeatherDefaultCity = current.Name;
        RefreshDefaultFlags();
        NoticeText = $"「{current.Name}」已设为默认城市";
        return true;
    }

    [RelayCommand]
    private void SetDefaultCityFromUi() => SetDefaultCity();

    public ObservableCollection<WeatherCityRow> Cities { get; } = new();

    public ObservableCollection<WeatherDayRow> Days { get; } = new();

    /// <summary>当前选中的城市名（界面上的 📍 标签）。</summary>
    public string CurrentCity => SelectedCity?.Name ?? "未选择城市";

    /// <summary>城市来源说明：内置坐标 / 在线解析 / 区县按所属市查询。</summary>
    public string CurrentCityNote
    {
        get
        {
            var city = SelectedCity?.Name;
            if (string.IsNullOrEmpty(city))
            {
                return string.Empty;
            }

            // 区县查不到时按所属地级市查的：明确告诉用户看的是哪个市的数据
            if (_report is { } report
                && !string.IsNullOrEmpty(report.CityName)
                && !string.Equals(report.CityName, city, StringComparison.Ordinal))
            {
                return $"（按 {report.CityName}）";
            }

            // 内置坐标表 / 省市区县表能离线定位的都算"内置坐标"
            return WeatherService.TryResolveOffline(city, out _, out _, out _)
                ? "（内置坐标）"
                : "（在线解析）";
        }
    }

    /// <summary>可以移除当前城市（至少得留一个）。</summary>
    public bool CanRemoveCity => Cities.Count > 1;

    /// <summary>输入框里待添加的城市名。</summary>
    [ObservableProperty]
    private string _newCityName = string.Empty;

    [ObservableProperty]
    private WeatherCityRow? _selectedCity;

    /// <summary>最后一次操作的提示（添加失败原因等）。</summary>
    [ObservableProperty]
    private string _noticeText = string.Empty;

    /// <summary>城市管理行（输入框 / 添加 / 设为默认 / 移除）是否展开；默认收起。</summary>
    [ObservableProperty]
    private bool _isCityEditorOpen;

    /// <summary>展开按钮上的文字。</summary>
    public string CityEditorToggleText => IsCityEditorOpen ? "收起" : "＋ 城市";

    partial void OnIsCityEditorOpenChanged(bool value) => OnPropertyChanged(nameof(CityEditorToggleText));

    public void ToggleCityEditor() => IsCityEditorOpen = !IsCityEditorOpen;

    [ObservableProperty]
    private string _boardName = "天气";

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private string _currentTempText = "--°C";

    [ObservableProperty]
    private string _currentText = string.Empty;

    [ObservableProperty]
    private string _currentIcon = "・";

    [ObservableProperty]
    private string _airQualityText = "--";

    [ObservableProperty]
    private string _airQualityIndexText = "--";

    [ObservableProperty]
    private string _currentWind = "--";

    [ObservableProperty]
    private string _humidityText = "--";

    /// <summary>加载中 / 失败时显示整层覆盖提示。</summary>
    [ObservableProperty]
    private bool _isBusyOverlayVisible = true;

    [ObservableProperty]
    private string _busyText = "正在获取天气…";

    [ObservableProperty]
    private string? _errorText;

    partial void OnSelectedCityChanged(WeatherCityRow? value)
    {
        if (value is not null)
        {
            foreach (var city in Cities)
            {
                city.IsSelected = ReferenceEquals(city, value);
            }

            _ = LoadAsync();
        }

        OnPropertyChanged(nameof(CurrentCity));
        OnPropertyChanged(nameof(CurrentCityNote));
        OnPropertyChanged(nameof(CanRemoveCity));
    }

    public void SelectCity(string name)
    {
        var target = Cities.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            SelectedCity = target;
        }
    }

    /// <summary>把当前城市列表写回设置（桌面时钟轮播用的也是这一份）。</summary>
    private void PersistCities()
    {
        _settings.WeatherCities = Cities.Select(c => c.Name).ToList();
    }

    /// <summary>添加城市；重名 / 空名 / 超限都会给出提示。</summary>
    public bool AddCity(string name)
    {
        var clean = (name ?? string.Empty).Trim();

        if (clean.Length == 0)
        {
            NoticeText = "请输入城市名";
            return false;
        }

        if (clean.Length > 12)
        {
            NoticeText = "城市名太长（最多 12 个字）";
            return false;
        }

        if (Cities.Any(c => string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            NoticeText = $"「{clean}」已经在列表里了";
            return false;
        }

        if (Cities.Count >= MaxCities)
        {
            NoticeText = $"最多 {MaxCities} 个城市，先移除一个";
            return false;
        }

        var row = new WeatherCityRow(clean);
        Cities.Add(row);
        PersistCities();
        RefreshDefaultFlags();
        NoticeText = $"已添加「{clean}」";
        SelectedCity = row;
        return true;
    }

    /// <summary>移除当前城市（最后一个不让删）。</summary>
    public void RemoveSelectedCity()
    {
        var current = SelectedCity;
        if (current is null || Cities.Count <= 1)
        {
            NoticeText = "至少要保留一个城市";
            return;
        }

        var index = Cities.IndexOf(current);
        Cities.Remove(current);

        // 删掉的正好是默认城市：清掉设置，自动回落到列表第一个
        if (string.Equals(_settings.WeatherDefaultCity, current.Name, StringComparison.Ordinal))
        {
            _settings.WeatherDefaultCity = null;
        }

        PersistCities();
        RefreshDefaultFlags();
        NoticeText = $"已移除「{current.Name}」";

        var next = Cities.Count == 0 ? null : Cities[Math.Min(index, Cities.Count - 1)];
        SelectedCity = next;
        if (next is null)
        {
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private void AddCityFromInput()
    {
        if (AddCity(NewCityName))
        {
            NewCityName = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveCity() => RemoveSelectedCity();

    [RelayCommand]
    private Task LoadAsync() => LoadCoreAsync();

    /// <summary>正在请求中的城市（同一城市的并发请求合并成一个）。</summary>
    private string? _loadingCity;

    /// <summary>请求序号：只让"最后一次点击"的结果落到界面上。</summary>
    private int _requestId;

    public async Task LoadCoreAsync()
    {
        var city = SelectedCity?.Name ?? Cities.FirstOrDefault()?.Name;
        if (string.IsNullOrEmpty(city))
        {
            return;
        }

        if (string.Equals(_loadingCity, city, StringComparison.Ordinal))
        {
            return;
        }

        var request = ++_requestId;
        _loadingCity = city;

        ErrorText = null;
        IsBusyOverlayVisible = true;
        BusyText = $"正在获取 {city} 的天气…";

        WeatherFetchOutcome outcome;
        try
        {
            outcome = await Task.Run(() => _weather.FetchWithOutcomeAsync(city));
        }
        finally
        {
            if (request == _requestId)
            {
                _loadingCity = null;
            }
        }

        // 期间用户又点了别的城市：这次结果作废
        if (request != _requestId)
        {
            return;
        }

        if (outcome.Report is null || outcome.Report.Days.Count == 0)
        {
            // 查不到 / 没有数据：明确提示，不拿别的城市顶上
            _report = null;
            OnPropertyChanged(nameof(CurrentCityNote));
            IsBusyOverlayVisible = true;
            BusyText = $"「{city}」暂无天气数据";
            NoticeText = outcome.Error ?? string.Empty;
            return;
        }

        Apply(outcome.Report);
        IsBusyOverlayVisible = false;
    }

    /// <summary>输入框的模糊搜索建议（全国省市区县表，最多 8 条）。</summary>
    public IReadOnlyList<DivisionSuggestion> SuggestCities(string query) => ChinaDivisions.Search(query, 8);

    private void Apply(WeatherReport report)
    {
        _report = report;
        UpdatedText = $"{DateTime.Now:HH:mm:ss} 更新";

        var today = report.Days.FirstOrDefault(d => d.DayLabel == "今天") ?? report.Days.FirstOrDefault();
        if (today is not null)
        {
            CurrentTempText = $"{today.TempMax:0}°C";
            CurrentText = today.Text;
            CurrentIcon = WeatherGlyph.For(today.Icon);
        }

        AirQualityText = report.AirQualityText ?? "--";
        AirQualityIndexText = report.AirQualityIndex?.ToString() ?? "--";
        CurrentWind = report.CurrentWind ?? "--";
        HumidityText = report.Humidity is { } h ? $"{h}%" : "--";

        Days.Clear();
        foreach (var day in report.Days)
        {
            Days.Add(new WeatherDayRow(day));
        }

        // 区县按所属市查的时候，来源说明要跟着变
        OnPropertyChanged(nameof(CurrentCityNote));
    }
}

/// <summary>世界时钟的一行。</summary>
public sealed partial class WorldClockRow : ObservableObject
{
    private readonly TimeZoneInfo _zone;
    private readonly DispatcherTimer _timer;

    public WorldClockRow(string cityName, string countryName, string timeZoneId)
    {
        CityName = cityName;
        CountryName = countryName;

        try
        {
            _zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception)
        {
            // IANA 名在 Windows 上找不到时的兜底（用固定 UTC 偏移近似，不含夏令时）
            _zone = timeZoneId switch
            {
                "America/New_York" => TimeZoneInfo.CreateCustomTimeZone("est", new TimeSpan(-5, 0, 0), "EST", "EST"),
                "America/Los_Angeles" => TimeZoneInfo.CreateCustomTimeZone("pst", new TimeSpan(-8, 0, 0), "PST", "PST"),
                "Europe/London" => TimeZoneInfo.CreateCustomTimeZone("gmt", TimeSpan.Zero, "GMT", "GMT"),
                "Europe/Paris" => TimeZoneInfo.CreateCustomTimeZone("cet", new TimeSpan(1, 0, 0), "CET", "CET"),
                "Europe/Moscow" => TimeZoneInfo.CreateCustomTimeZone("msk", new TimeSpan(3, 0, 0), "MSK", "MSK"),
                "Asia/Tokyo" => TimeZoneInfo.CreateCustomTimeZone("jst", new TimeSpan(9, 0, 0), "JST", "JST"),
                "Australia/Sydney" => TimeZoneInfo.CreateCustomTimeZone("aest", new TimeSpan(10, 0, 0), "AEST", "AEST"),
                "Asia/Kolkata" => TimeZoneInfo.CreateCustomTimeZone("ist", new TimeSpan(5, 30, 0), "IST", "IST"),
                "America/Sao_Paulo" => TimeZoneInfo.CreateCustomTimeZone("brt", new TimeSpan(-3, 0, 0), "BRT", "BRT"),
                "Africa/Lagos" => TimeZoneInfo.CreateCustomTimeZone("wat", new TimeSpan(1, 0, 0), "WAT", "WAT"),
                _ => TimeZoneInfo.Utc,
            };
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public string CityName { get; }

    public string CountryName { get; }

    public string TimeText { get; private set; } = string.Empty;

    public string DateText { get; private set; } = string.Empty;

    private void Refresh()
    {
        var now = TimeZoneInfo.ConvertTime(DateTime.Now, _zone);
        TimeText = now.ToString("HH:mm:ss");
        DateText = now.ToString("M月d日 ddd");
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DateText));
    }
}

/// <summary>世界时钟看板：全球十大国家代表城市。</summary>
public sealed class WorldClockViewModel
{
    public static readonly IReadOnlyList<(string City, string Country, string WindowsZone)> DefaultCities =
    [
        ("北京", "中国", "China Standard Time"),
        ("东京", "日本", "Tokyo Standard Time"),
        ("首尔", "韩国", "Korea Standard Time"),
        ("新加坡", "新加坡", "Singapore Standard Time"),
        ("伦敦", "英国", "GMT Standard Time"),
        ("巴黎", "法国", "Romance Standard Time"),
        ("莫斯科", "俄罗斯", "Russian Standard Time"),
        ("纽约", "美国", "Eastern Standard Time"),
        ("洛杉矶", "美国", "Pacific Standard Time"),
        ("悉尼", "澳大利亚", "AUS Eastern Standard Time"),
    ];

    public ObservableCollection<WorldClockRow> Clocks { get; } = new();

    public WorldClockViewModel()
    {
        foreach (var (city, country, zone) in DefaultCities)
        {
            Clocks.Add(new WorldClockRow(city, country, zone));
        }
    }
}

/// <summary>
/// 桌面悬浮时钟：阳历 + 农历（含节气/节日）+ 时间，下面是今日天气和今日日程——
/// 点天气行打开天气看板、点日程行打开日历看板。
/// </summary>
public sealed partial class DesktopClockViewModel : ObservableObject
{
    private readonly WeatherService _weather;
    private readonly AppSettingsService _settings;
    private readonly TodoService _todos;
    private readonly WindowManager _windows;
    private readonly DispatcherTimer _timer;

    private IReadOnlyList<string> _cities;
    private int _currentCityIndex;
    private int _rotation;
    private WeatherReport? _report;
    private bool _fetching;

    /// <summary>当前显示的是哪一天：跨天后农历、节气、日程都要整体重算。</summary>
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);

    private string _lunarText = string.Empty;

    public DesktopClockViewModel(
        WeatherService weather,
        AppSettingsService settings,
        TodoService todos,
        WindowManager windows)
    {
        _weather = weather;
        _settings = settings;
        _todos = todos;
        _windows = windows;
        _cities = settings.WeatherCities;
        _currentCityIndex = DefaultCityIndex();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start()
    {
        RefreshDayTexts();
        _timer.Start();
        _ = LoadWeatherAsync();
    }

    public void Stop() => _timer.Stop();

    public string SolarDateText => DateTime.Now.ToString("yyyy年M月d日");

    public string WeekdayText => DateTime.Now.ToString("dddd");

    public string TimeText => DateTime.Now.ToString("HH:mm:ss");

    /// <summary>农历 + 干支 + 生肖；今天有节日/节气就追加在末尾（每天只算一次）。</summary>
    public string LunarText => _lunarText;

    // ---------- 今日日程（点一下打开日历） ----------

    [ObservableProperty]
    private string _agendaLine = "正在读取日程…";

    [ObservableProperty]
    private string _agendaDetail = string.Empty;

    private void RefreshDayTexts()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _day = today;

        _lunarText = BuildLunarText(today);
        OnPropertyChanged(nameof(LunarText));

        RefreshAgenda(today);
    }

    private static string BuildLunarText(DateOnly day)
    {
        var lunar = LunarCalendar.SolarToLunar(day);
        var text = lunar.IsValid
            ? $"农历{lunar.MonthName}月{lunar.DayName} · {LunarCalendar.YearGanZhi(lunar.Year)}年{LunarCalendar.YearZodiac(lunar.Year)}年"
            : string.Empty;

        // 节日优先于节气（节日更容易和"今天的安排"对上）；都没有就不追加
        var almanac = Almanac.Describe(day);
        var extra = !string.IsNullOrEmpty(almanac.HolidayName)
            ? almanac.HolidayName
            : almanac.SolarTerm;

        if (string.IsNullOrEmpty(extra))
        {
            return text;
        }

        return text.Length == 0 ? extra : text + " · " + extra;
    }

    private void RefreshAgenda(DateOnly day)
    {
        AgendaSnapshot snapshot;
        try
        {
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
            var to = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue)).AddTicks(-1);
            snapshot = AgendaSummary.Build(
                _todos.GetCalendarEntries(from, to, includeDone: true), day, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            AgendaLine = "日程读取失败";
            AgendaDetail = ex.Message;
            return;
        }

        if (snapshot.IsEmpty)
        {
            AgendaLine = "今天没有安排";
            AgendaDetail = "点击打开日历";
            return;
        }

        if (snapshot.AllDone)
        {
            AgendaLine = $"今日 {snapshot.Total} 项 · 已全部完成";
            AgendaDetail = string.Empty;
            return;
        }

        var next = snapshot.Next!;
        var time = next.Occurrence.LocalDateTime.TimeOfDay == TimeSpan.Zero
            ? string.Empty
            : next.Occurrence.LocalDateTime.ToString("HH:mm") + " ";

        AgendaLine = $"今日 {snapshot.Total} 项 · {time}{Clip(next.Todo.Title)}";
        AgendaDetail = $"已完成 {snapshot.Done}/{snapshot.Total}";
    }

    /// <summary>标题太长会把这一行挤爆，截到 12 个字。</summary>
    private static string Clip(string text, int max = 12)
    {
        var clean = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }

    /// <summary>点天气行：打开天气看板（还没有就建一个）。</summary>
    public void OpenWeatherBoard() => _windows.OpenBoardByKind(BoardKind.Weather, "天气");

    /// <summary>点日程行：打开日历看板并跳回今天。</summary>
    public void OpenCalendarBoard() => _windows.OpenBoardByKind(BoardKind.Calendar, "日历");

    // ---------- 天气轮播 ----------

    [ObservableProperty]
    private string _weatherLine = "正在获取天气…";

    [ObservableProperty]
    private string _weatherDetail = string.Empty;

    /// <summary>天气是否处于失败态（失败时降低重试频率）。</summary>
    private bool _weatherFailed;

    private async Task TickAsync()
    {
        OnPropertyChanged(nameof(SolarDateText));
        OnPropertyChanged(nameof(WeekdayText));
        OnPropertyChanged(nameof(TimeText));

        _rotation++;

        // 跨天：农历/节气/日程一起换；没跨天就每 30 秒刷一次日程
        if (DateOnly.FromDateTime(DateTime.Now) != _day)
        {
            RefreshDayTexts();
        }
        else if (_rotation % 30 == 0)
        {
            RefreshAgenda(_day);
        }

        // 天气数据每 30 分钟刷新一次；失败时 60 秒后重试
        if ((!_weatherFailed && _rotation % 1800 == 0) || (_weatherFailed && _rotation % 60 == 0))
        {
            await LoadWeatherAsync();
            return;
        }

        UpdateWeatherLine();
    }

    private void UpdateWeatherLine()
    {
        if (_report is null || _report.Days.Count == 0)
        {
            return;
        }

        // 只看默认城市（没有默认就用列表第一个）；区县查不到时会自动用所属市，
        // 这里显示的是实际查到数据的城市名。
        var day = _report.Days.FirstOrDefault(d => d.DayLabel == "今天") ?? _report.Days[0];
        WeatherLine = $"{_report.CityName} {day.TempMax:0}°C {day.Text}";
        WeatherDetail = BuildWeatherDetail(day);
    }

    /// <summary>默认城市在列表里的下标（没设置 / 找不到就是 0）。</summary>
    private int DefaultCityIndex()
    {
        var defaultCity = _settings.EffectiveWeatherDefaultCity;
        if (string.IsNullOrEmpty(defaultCity))
        {
            return 0;
        }

        for (var i = 0; i < _cities.Count; i++)
        {
            if (string.Equals(_cities[i], defaultCity, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>风况 + 降水（概率高才提）或湿度，一行放得下。</summary>
    private string BuildWeatherDetail(WeatherDayInfo day)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(day.Wind))
        {
            parts.Add(day.Wind);
        }

        if (day.PrecipitationProbability >= 30)
        {
            parts.Add($"降水{day.PrecipitationProbability}%");
        }
        else if (_report?.Humidity is { } humidity)
        {
            parts.Add($"湿度{humidity}%");
        }

        return string.Join(" · ", parts);
    }

    private async Task LoadWeatherAsync()
    {
        if (_fetching || _cities.Count == 0)
        {
            return;
        }

        _fetching = true;

        try
        {
            // 重新读设置：用户在天气看板里加/删城市、改默认城市后立刻跟上
            _cities = _settings.WeatherCities;
            _currentCityIndex = DefaultCityIndex();

            if (_cities.Count == 0)
            {
                WeatherLine = "还没有城市";
                WeatherDetail = "在天气看板里添加";
                return;
            }

            // 只看默认城市：查不到就明确说"暂无数据"，不拿别的城市的天气顶上
            var city = _cities[Math.Min(_currentCityIndex, _cities.Count - 1)];
            var outcome = await Task.Run(() => _weather.FetchWithOutcomeAsync(city));

            if (outcome.Success && outcome.Report is { Days.Count: > 0 })
            {
                _report = outcome.Report;
                _weatherFailed = false;
                UpdateWeatherLine();
            }
            else
            {
                _report = null;
                _weatherFailed = true;
                WeatherLine = $"{city} 暂无天气数据";
                WeatherDetail = outcome.Error ?? "稍后自动重试";
            }
        }
        catch (Exception)
        {
            _weatherFailed = true;
            _report = null;
            WeatherLine = "天气暂无数据";
            WeatherDetail = "稍后自动重试";
        }
        finally
        {
            _fetching = false;
        }
    }
}
