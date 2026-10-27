using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Services;
using StickyTodo.Core.Calendar;
using StickyTodo.Weather;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 桌面悬浮时钟（桌面挂件）：阳历 + 农历 + 时间，天气就绪时轮播天气。
/// 刻意做成小窗、半透明、置顶、不进任务栏，悬浮在桌面角落。
/// </summary>
public sealed partial class DesktopClockViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private readonly WeatherService _weather;
    private readonly AppSettingsService _settings;

    private IReadOnlyList<string> _cities;
    private int _currentCityIndex;
    private WeatherReport? _report;
    private bool _weatherReady;
    private int _rotation;

    /// <summary>天气全列表失败后的下次重试时间；null = 不需要急着重试。</summary>
    private DateTime? _failedUntil;

    public DesktopClockViewModel(WeatherService weather, AppSettingsService settings)
    {
        _weather = weather;
        _settings = settings;
        _cities = settings.WeatherCities;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    /// <summary>时钟开始走 + 触发首次天气加载。</summary>
    public void Start()
    {
        _timer.Start();
        _ = LoadWeatherAsync();
    }

    public void Stop() => _timer.Stop();

    // ---------- 每秒刷新的时间文本 ----------

    public string SolarDateText => DateTime.Now.ToString("yyyy年M月d日");

    public string WeekdayText => DateTime.Now.ToString("dddd", new System.Globalization.CultureInfo("zh-CN"));

    public string TimeText => DateTime.Now.ToString("HH:mm:ss");

    public string LunarText
    {
        get
        {
            var lunar = LunarCalendar.SolarToLunar(DateOnly.FromDateTime(DateTime.Now));
            if (!lunar.IsValid)
            {
                return string.Empty;
            }

            return $"农历{lunar.MonthName}月{lunar.DayName} · {LunarCalendar.YearGanZhi(lunar.Year)}年{LunarCalendar.YearZodiac(lunar.Year)}年";
        }
    }

    // ---------- 天气轮播 ----------

    /// <summary>轮播内容：前 9 秒显示天气，之后 3 秒显示城市名，循环。</summary>
    [ObservableProperty]
    private string _weatherLine = "正在获取天气…";

    [ObservableProperty]
    private string _weatherDetail = string.Empty;

    [ObservableProperty]
    private string _cityText = string.Empty;

    private async Task TickAsync()
    {
        OnPropertyChanged(nameof(SolarDateText));
        OnPropertyChanged(nameof(WeekdayText));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(LunarText));

        // 每 10 秒换一次显示内容（天气 ↔ 城市名轮换）；每 30 分钟刷新天气数据
        _rotation++;
        UpdateWeatherLine();

        if (_failedUntil is { } until && DateTime.Now < until)
        {
            return;
        }

        if (_rotation % 1800 == 0 || _failedUntil is not null)
        {
            await LoadWeatherAsync();
        }
    }

    private void UpdateWeatherLine()
    {
        if (!_weatherReady || _report is null || _report.Days.Count == 0)
        {
            return;
        }

        var cycle = _rotation % 12;
        if (cycle < 9)
        {
            // 前 9 秒：当前城市的今天天气
            var day = _report.Days.FirstOrDefault(d => d.DayLabel == "今天") ?? _report.Days[0];
            WeatherLine = $"{_report.CityName} {day.TempMax:0}°C {day.Text}";
            WeatherDetail = day.Wind;
        }
        else
        {
            // 后 3 秒：显示城市列表里当前选中的城市
            var city = _cities.Count > 0 ? _cities[_currentCityIndex % _cities.Count] : string.Empty;
            WeatherLine = city;
            WeatherDetail = "点击切换城市";
        }
    }

    private async Task LoadWeatherAsync()
    {
        // 每次都重新读设置：用户在天气看板里加/删城市后，轮播立刻跟上
        _cities = _settings.WeatherCities;

        if (_cities.Count == 0)
        {
            WeatherLine = "没有可显示的城市";
            WeatherDetail = "在天气看板里添加城市";
            return;
        }

        // 逐个试：内置坐标的城市基本不会失败；在线解析查不到的跳过试下一个
        WeatherFetchOutcome? outcome = null;

        for (var attempt = 0; attempt < _cities.Count; attempt++)
        {
            var city = _cities[_currentCityIndex % _cities.Count];
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
            _weatherReady = true;
            _failedUntil = null;
            UpdateWeatherLine();
            return;
        }

        // 全列表都失败：60 秒后再试，别一秒一秒地砸接口
        _failedUntil = DateTime.Now.AddSeconds(60);
        WeatherLine = "天气获取失败";
        WeatherDetail = "60 秒后自动重试";
    }
}
