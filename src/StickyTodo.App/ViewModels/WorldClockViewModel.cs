using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Windows.Threading;
using StickyTodo.App.Services;

namespace StickyTodo.App.ViewModels;

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
        catch (TimeZoneNotFoundException)
        {
            // Windows 与 IANA 时区名不一致时的兜底（用 UTC 偏移近似）
            _zone = FallbackZone(timeZoneId);
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
        DateText = now.ToString("M月d日 ddd", new System.Globalization.CultureInfo("zh-CN"));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DateText));
    }

    /// <summary>Windows 时区 ID 兜底：按已知 IANA 名映射到 UTC 偏移。</summary>
    private static TimeZoneInfo FallbackZone(string ianaId) => ianaId switch
    {
        "America/New_York" => TimeZoneInfo.CreateCustomTimeZone("est", TimeSpan.FromHours(-5), "EST", "EST"),
        "America/Los_Angeles" => TimeZoneInfo.CreateCustomTimeZone("pst", TimeSpan.FromHours(-8), "PST", "PST"),
        "Europe/London" => TimeZoneInfo.CreateCustomTimeZone("gmt", TimeSpan.FromHours(0), "GMT", "GMT"),
        "Europe/Paris" => TimeZoneInfo.CreateCustomTimeZone("cet", TimeSpan.FromHours(1), "CET", "CET"),
        "Europe/Moscow" => TimeZoneInfo.CreateCustomTimeZone("msk", TimeSpan.FromHours(3), "MSK", "MSK"),
        "Asia/Tokyo" => TimeZoneInfo.CreateCustomTimeZone("jst", TimeSpan.FromHours(9), "JST", "JST"),
        "Australia/Sydney" => TimeZoneInfo.CreateCustomTimeZone("aest", TimeSpan.FromHours(10), "AEST", "AEST"),
        "Asia/Kolkata" => TimeZoneInfo.CreateCustomTimeZone("ist", TimeSpan.FromHours(5), "IST", "IST"),
        "America/Sao_Paulo" => TimeZoneInfo.CreateCustomTimeZone("brt", TimeSpan.FromHours(-3), "BRT", "BRT"),
        "Africa/Lagos" => TimeZoneInfo.CreateCustomTimeZone("wat", TimeSpan.FromHours(1), "WAT", "WAT"),
        _ => TimeZoneInfo.Utc,
    };
}

/// <summary>世界时钟看板。</summary>
public sealed class WorldClockViewModel : ObservableObject
{
    /// <summary>全球十大国家代表城市（用户最常问的时区）。</summary>
    public static readonly IReadOnlyList<(string City, string Country, string Iana)> DefaultCities =
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
