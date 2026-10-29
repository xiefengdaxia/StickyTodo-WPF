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

        RefreshDefaultFlags();
        _boardName = "天气";
    }

    /// <summary>选中默认城市（打开看板时用；没设过就是列表第一个）。</summary>
    public void SelectDefaultCity()
    {
        var defaultCity = _settings.EffectiveWeatherDefaultCity;

        SelectedCity = Cities.FirstOrDefault(c => string.Equals(c.Name, defaultCity, StringComparison.Ordinal))
                       ?? Cities.FirstOrDefault();
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

    /// <summary>把"默认"标记刷到城市胶囊上（★）。</summary>
    private void RefreshDefaultFlags()
    {
        var defaultCity = _settings.EffectiveWeatherDefaultCity;

        foreach (var city in Cities)
        {
            city.IsDefault = string.Equals(city.Name, defaultCity, StringComparison.Ordinal);
        }
    }

    [ObservableProperty]
    private string _boardName;

    /// <summary>城市数量上限（再多加界面就排不下了）。</summary>
    private const int MaxCities = 12;

    public ObservableCollection<WeatherCityViewModel> Cities { get; } = new();

    public ObservableCollection<WeatherDayRow> Days { get; } = new();

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

    /// <summary>至少留一个城市才允许移除。</summary>
    public bool CanRemoveCity => Cities.Count > 1;

    /// <summary>操作提示（添加 / 移除结果）。</summary>
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
        OnPropertyChanged(nameof(CurrentCityNote));
        OnPropertyChanged(nameof(CanRemoveCity));

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

    /// <summary>把当前城市列表写回设置（桌面时钟轮播用同一份）。</summary>
    private void PersistCities()
    {
        _settings.WeatherCities = Cities.Select(c => c.Name).ToList();
    }

    /// <summary>添加城市；空名 / 重名 / 超长 / 超限都会给出提示。</summary>
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

        var row = new WeatherCityViewModel(clean);
        Cities.Add(row);
        PersistCities();
        RefreshDefaultFlags();
        OnPropertyChanged(nameof(CanRemoveCity));
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
        OnPropertyChanged(nameof(CanRemoveCity));
        NoticeText = $"已移除「{current.Name}」";

        SelectedCity = Cities.Count == 0 ? null : Cities[Math.Min(index, Cities.Count - 1)];
        if (SelectedCity is null)
        {
            _ = LoadAsync();
        }
    }

    /// <summary>正在请求中的城市（同一城市的并发请求合并成一个）。</summary>
    private string? _loadingCity;

    /// <summary>请求序号：只让"最后一次点击"的结果落到界面上。</summary>
    private int _requestId;

    public async Task LoadAsync()
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

        _report = outcome.Report;
        Apply(_report);
        IsBusyOverlayVisible = false;
    }

    /// <summary>输入框的模糊搜索建议（全国省市区县表，最多 8 条）。</summary>
    public IReadOnlyList<DivisionSuggestion> SuggestCities(string query) => ChinaDivisions.Search(query, 8);

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

        // 区县按所属市查的时候，来源说明要跟着变
        OnPropertyChanged(nameof(CurrentCityNote));
    }
}

/// <summary>城市胶囊。</summary>
public sealed partial class WeatherCityViewModel : ObservableObject
{
    public WeatherCityViewModel(string name) => Name = name;

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
