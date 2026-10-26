using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;
using System.Windows.Input;
using StickyTodo.Core.Services;
using StickyTodo.Weather;

namespace StickyTodo.App.ViewModels;

/// <summary>天气看板的一行（=一天）。</summary>
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

    public string IconGlyph { get; }

    public string IconGlyph2 { get; }
}

/// <summary>天气现象 → 文字符号（不依赖图片资源，任何 DPI 下都清晰）。</summary>
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
        "snow" => "❄",
        "snow-light" => "🌨",
        "snow-heavy" => "❄",
        "thunder" => "⛈",
        _ => "・",
    };
}

/// <summary>天气看板的 ViewModel。</summary>
public sealed partial class WeatherViewModel : ObservableObject
{
    private readonly WeatherService _weather;
    private readonly AppSettingsService _settings;

    private WeatherReport? _report;

    public WeatherViewModel(WeatherService weather, AppSettingsService settings)
    {
        _weather = weather;
        _settings = settings;

        foreach (var city in settings.WeatherCities)
        {
            Cities.Add(new WeatherCityViewModel(city));
        }

        _boardName = "天气";
    }

    [ObservableProperty]
    private string _boardName;

    public ObservableCollection<WeatherCityViewModel> Cities { get; } = new();

    public ObservableCollection<WeatherDayRow> Days { get; } = new();

    public string CurrentCity => SelectedCity?.Name ?? string.Empty;

    [ObservableProperty]
    private WeatherCityViewModel? _selectedCity;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private string _currentTempText = "--°C";

    [ObservableProperty]
    private string _currentText = string.Empty;

    [ObservableProperty]
    private string _currentIconGlyph = "・";

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

    partial void OnSelectedCityChanged(WeatherCityViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentCity));

        if (value is not null)
        {
            foreach (var city in Cities)
            {
                city.IsSelected = ReferenceEquals(city, value);
            }

            _ = LoadAsync();
        }
    }

    /// <summary>切换城市（供窗口点击调用）。</summary>
    public void SelectCity(string name)
    {
        var target = Cities.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            SelectedCity = target;
        }
    }

    public async Task LoadAsync()
    {
        var city = SelectedCity?.Name ?? Cities.FirstOrDefault()?.Name;
        if (string.IsNullOrEmpty(city))
        {
            return;
        }

        IsBusyOverlayVisible = true;
        BusyText = $"正在获取 {city} 的天气…";

        var report = await Task.Run(() => _weather.FetchAsync(city));

        if (report is null)
        {
            BusyText = $"获取 {city} 的天气失败，请检查网络后重试";
            return;
        }

        _report = report;
        Apply(report);
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
            CurrentIconGlyph = WeatherGlyph.For(today.Icon);
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

/// <summary>城市胶囊。</summary>
public sealed partial class WeatherCityViewModel : ObservableObject
{
    public WeatherCityViewModel(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}
