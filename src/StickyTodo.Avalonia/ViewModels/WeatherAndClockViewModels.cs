using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyTodo.Core.Services;
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
}

/// <summary>天气看板。</summary>
public sealed partial class WeatherViewModel : ObservableObject
{
    /// <summary>城市数量上限（再多加界面就排不下了）。</summary>
    private const int MaxCities = 12;

    private readonly WeatherService _weather;
    private readonly AppSettingsService _settings;

    public WeatherViewModel(WeatherService weather, AppSettingsService settings)
    {
        _weather = weather;
        _settings = settings;

        foreach (var city in settings.WeatherCities)
        {
            Cities.Add(new WeatherCityRow(city));
        }

        SelectedCity = Cities.FirstOrDefault();
    }

    public ObservableCollection<WeatherCityRow> Cities { get; } = new();

    public ObservableCollection<WeatherDayRow> Days { get; } = new();

    /// <summary>当前选中的城市名（界面上的 📍 标签）。</summary>
    public string CurrentCity => SelectedCity?.Name ?? "未选择城市";

    /// <summary>城市来源说明：内置坐标 / 在线解析。</summary>
    public string CurrentCityNote
    {
        get
        {
            var city = SelectedCity?.Name;
            if (string.IsNullOrEmpty(city))
            {
                return string.Empty;
            }

            return WeatherService.IsKnownCity(city) ? "（内置坐标）" : "（在线解析）";
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
        PersistCities();
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

        if (outcome.Report is null)
        {
            IsBusyOverlayVisible = true;
            BusyText = $"获取 {city} 的天气失败：{outcome.Error}";
            return;
        }

        Apply(outcome.Report);
        IsBusyOverlayVisible = false;
    }

    private void Apply(WeatherReport report)
    {
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

/// <summary>桌面悬浮时钟：阳历 + 农历 + 时间 + 天气轮播。</summary>
public sealed partial class DesktopClockViewModel : ObservableObject
{
    private readonly WeatherService _weather;
    private readonly AppSettingsService _settings;
    private readonly DispatcherTimer _timer;

    private IReadOnlyList<string> _cities;
    private int _currentCityIndex;
    private int _rotation;
    private WeatherReport? _report;
    private bool _fetching;

    public DesktopClockViewModel(WeatherService weather, AppSettingsService settings)
    {
        _weather = weather;
        _settings = settings;
        _cities = settings.WeatherCities;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = LoadWeatherAsync();
    }

    public void Stop() => _timer.Stop();

    public string SolarDateText => DateTime.Now.ToString("yyyy年M月d日");

    public string WeekdayText => DateTime.Now.ToString("dddd");

    public string TimeText => DateTime.Now.ToString("HH:mm:ss");

    public string LunarText
    {
        get
        {
            var lunar = Core.Calendar.LunarCalendar.SolarToLunar(DateOnly.FromDateTime(DateTime.Now));
            if (!lunar.IsValid)
            {
                return string.Empty;
            }

            return $"农历{lunar.MonthName}月{lunar.DayName} · {Core.Calendar.LunarCalendar.YearGanZhi(lunar.Year)}年{Core.Calendar.LunarCalendar.YearZodiac(lunar.Year)}年";
        }
    }

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
        OnPropertyChanged(nameof(LunarText));

        _rotation++;

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

        // 前 9 秒显示当前城市天气，之后 3 秒显示下一个城市名（提示可切换）
        var cycle = _rotation % 12;
        if (cycle < 9)
        {
            var day = _report.Days.FirstOrDefault(d => d.DayLabel == "今天") ?? _report.Days[0];
            WeatherLine = $"{_report.CityName} {day.TempMax:0}°C {day.Text}";
            WeatherDetail = day.Wind;
        }
        else
        {
            WeatherLine = _cities.Count > 0 ? _cities[_currentCityIndex % _cities.Count] : string.Empty;
            WeatherDetail = "轮播";
        }
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
            // 重新读设置：用户在天气看板里加/删城市后轮播立刻跟上
            _cities = _settings.WeatherCities;

            // 逐个试：内置坐标的城市基本不会失败；查不到的（在线解析）跳过试下一个
            WeatherFetchOutcome? outcome = null;
            var city = string.Empty;

            for (var attempt = 0; attempt < _cities.Count; attempt++)
            {
                city = _cities[_currentCityIndex % _cities.Count];
                var result = await Task.Run(() => _weather.FetchWithOutcomeAsync(city));
                if (result.Success)
                {
                    outcome = result;
                    break;
                }

                _currentCityIndex = (_currentCityIndex + 1) % _cities.Count;
                outcome = result;
            }

            if (outcome?.Success == true)
            {
                _report = outcome.Report;
                _weatherFailed = false;
                UpdateWeatherLine();
            }
            else
            {
                _weatherFailed = true;
                WeatherLine = "天气获取失败";
                WeatherDetail = "稍后自动重试";
            }
        }
        catch (Exception)
        {
            _weatherFailed = true;
            WeatherLine = "天气获取失败";
            WeatherDetail = "稍后自动重试";
        }
        finally
        {
            _fetching = false;
        }
    }
}