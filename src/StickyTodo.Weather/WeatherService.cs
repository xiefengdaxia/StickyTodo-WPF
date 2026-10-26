using System.Text.Json;

namespace StickyTodo.Weather;

/// <summary>天气代码 → 描述与图标名。Open-Meteo 的 weathercode 定义，中文描述按 WMO 语义翻译。</summary>
public static class WeatherCode
{
    public readonly record struct Info(string Text, string Icon);

    public static Info Describe(int code) => code switch
    {
        0 => new Info("晴", "sunny"),
        1 => new Info("基本晴", "mostly-sunny"),
        2 => new Info("多云", "partly-cloudy"),
        3 => new Info("阴", "cloudy"),
        45 or 48 => new Info("雾", "fog"),
        51 or 53 or 55 => new Info("毛毛雨", "drizzle"),
        56 or 57 => new Info("冻毛毛雨", "sleet"),
        61 => new Info("小雨", "rain-light"),
        63 => new Info("中雨", "rain"),
        65 => new Info("大雨", "rain-heavy"),
        66 or 67 => new Info("冻雨", "sleet"),
        71 => new Info("小雪", "snow-light"),
        73 => new Info("中雪", "snow"),
        75 => new Info("大雪", "snow-heavy"),
        77 => new Info("雪粒", "snow"),
        80 => new Info("阵雨", "showers"),
        81 => new Info("强阵雨", "showers"),
        82 => new Info("暴雨", "rain-heavy"),
        85 or 86 => new Info("阵雪", "snow"),
        95 => new Info("雷阵雨", "thunder"),
        96 or 99 => new Info("雷阵雨伴冰雹", "thunder"),
        _ => new Info("未知", "unknown"),
    };

    /// <summary>风向角度 → 十六方位中文名。</summary>
    public static string WindDirection(double degrees)
    {
        // 0 = 北，顺时针。每 22.5° 一档。
        var names = new[]
        {
            "北", "北东北", "东北", "东东北", "东", "东东南", "东南", "南东南",
            "南", "南西南", "西南", "西西南", "西", "西西北", "西北", "北西北",
        };

        var index = (int)Math.Round(((degrees % 360) + 360) % 360 / 22.5) % 16;
        return names[index];
    }

    /// <summary>风速（km/h）→ 蒲福风级（0–12）中文名。</summary>
    public static string WindLevel(double kmh)
    {
        var bounds = new[] { 1d, 5, 11, 19, 28, 38, 49, 61, 74, 88, 102, 117 };
        for (var i = 0; i < bounds.Length; i++)
        {
            if (kmh < bounds[i])
            {
                return $"{i}级";
            }
        }

        return "12级";
    }
}

/// <summary>一天的天气（实时或预报）。</summary>
public sealed record WeatherDayInfo
{
    /// <summary>日期（当天为实时那天）。</summary>
    public DateOnly Date { get; init; }

    /// <summary>星期几的显示名，如「周三」；今天是「今天」，昨天是「昨天」。</summary>
    public string DayLabel { get; init; } = string.Empty;

    /// <summary>日期显示名，如「10月27日」。</summary>
    public string DateLabel { get; init; } = string.Empty;

    /// <summary>天气描述（晴 / 多云 / 小雨…）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>图标名（配合 WeatherIcon 选择绘制内容）。</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>最高温（°C）。实时天与最高温相同。</summary>
    public double TempMax { get; init; }

    /// <summary>最低温（°C）。</summary>
    public double TempMin { get; init; }

    /// <summary>风况描述，如「北风2级」。</summary>
    public string Wind { get; init; } = string.Empty;

    /// <summary>降水概率（%）。</summary>
    public int PrecipitationProbability { get; init; }
}

/// <summary>一个城市的完整天气。</summary>
public sealed record WeatherReport
{
    /// <summary>城市显示名（Open-Meteo 返回不到名字时用查询名）。</summary>
    public string CityName { get; init; } = string.Empty;

    /// <summary>纬度 / 经度（便于核对定位是否正确）。</summary>
    public double Latitude { get; init; }

    public double Longitude { get; init; }

    /// <summary>空气质量指数（1–5，1=优）。取不到为 null。</summary>
    public int? AirQualityIndex { get; init; }

    /// <summary>空气质量描述（优 / 良 / 轻度污染…）。</summary>
    public string? AirQualityText { get; init; }

    /// <summary>实时湿度（%）。</summary>
    public int? Humidity { get; init; }

    /// <summary>实时风向 / 风级文本，如「北风2级」。</summary>
    public string? CurrentWind { get; init; }

    /// <summary>昨天 + 今天 + 未来预报（昨天可能为空——接口只返回未来时）。</summary>
    public IReadOnlyList<WeatherDayInfo> Days { get; init; } = Array.Empty<WeatherDayInfo>();
}

/// <summary>
/// 天气数据访问：Open-Meteo 免费接口（无需注册、无 API key）。
///   地理编码: https://geocoding-api.open-meteo.com/v1/search?name=双峰&language=zh&count=1
///   天气:     https://api.open-meteo.com/v1/forecast?...&daily=...&current=...
///   空气质量: https://air-quality-api.open-meteo.com/v1/air-quality?...&hourly=...（ european_aqi 为整数）
///
/// 这是全应用唯一的联网功能，超时 8 秒、全部失败时返回 null，界面显示"获取失败"。
/// </summary>
public sealed class WeatherService
{
    private const string GeocodingUrl =
        "https://geocoding-api.open-meteo.com/v1/search?name={0}&language=zh&count=1";

    private const string ForecastUrl =
        "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
        "&daily=weather_code,temperature_2m_max,temperature_2m_min,wind_direction_10m_dominant,wind_speed_10m_max,precipitation_probability_max" +
        "&current=temperature_2m,relative_humidity_2m,weather_code,wind_direction_10m,wind_speed_10m" +
        "&past_days=1&forecast_days=6&timezone=auto";

    private const string AirQualityUrl =
        "https://air-quality-api.open-meteo.com/v1/air-quality?latitude={0}&longitude={1}" +
        "&current=european_aqi&timezone=auto";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WeatherReport?> FetchAsync(string city, CancellationToken cancellation = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            var (lat, lon, resolvedName) = await GeocodeAsync(http, city, cancellation);
            if (!lat.HasValue)
            {
                return null;
            }

            var forecast = await FetchForecastAsync(http, lat.Value, lon.Value, resolvedName, cancellation);
            if (forecast is null)
            {
                return null;
            }

            // 空气质量是加分项，失败不影响主数据
            var aqi = await FetchAirQualityAsync(http, lat.Value, lon.Value, cancellation);

            return forecast with
            {
                AirQualityIndex = aqi?.Index,
                AirQualityText = aqi?.Text,
            };
        }
        catch (Exception)
        {
            // 网络 / 解析失败都算"获取失败"，由界面提示
            return null;
        }
    }

    private static async Task<(double? Lat, double? Lon, string Name)> GeocodeAsync(
        HttpClient http, string city, CancellationToken cancellation)
    {
        var url = string.Format(GeocodingUrl, Uri.EscapeDataString(city));
        using var response = await http.GetAsync(url, cancellation);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellation);
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");

        if (results.GetArrayLength() == 0)
        {
            return (null, null, string.Empty);
        }

        var first = results[0];
        return (
            first.GetProperty("latitude").GetDouble(),
            first.GetProperty("longitude").GetDouble(),
            first.TryGetProperty("name", out var name) ? name.GetString() ?? city : city);
    }

    private static async Task<WeatherReport?> FetchForecastAsync(
        HttpClient http, double lat, double lon, string cityName, CancellationToken cancellation)
    {
        var url = string.Format(
            ForecastUrl,
            lat.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lon.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await http.GetAsync(url, cancellation);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellation);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var daily = root.GetProperty("daily");
        var times = daily.GetProperty("time");
        var codes = daily.GetProperty("weather_code");
        var tMax = daily.GetProperty("temperature_2m_max");
        var tMin = daily.GetProperty("temperature_2m_min");
        var windDir = daily.GetProperty("wind_direction_10m_dominant");
        var windSpeed = daily.GetProperty("wind_speed_10m_max");
        var precip = daily.GetProperty("precipitation_probability_max");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var days = new List<WeatherDayInfo>();

        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            var date = DateOnly.Parse(times[i].GetString()!);
            var code = codes[i].GetInt32();
            var info = WeatherCode.Describe(code);
            var windDirText = WeatherCode.WindDirection(windDir[i].GetDouble());
            var windLevel = WeatherCode.WindLevel(windSpeed[i].GetDouble());

            days.Add(new WeatherDayInfo
            {
                Date = date,
                DayLabel = date == today ? "今天" : date == today.AddDays(-1) ? "昨天" : WeekdayName(date),
                DateLabel = date.ToString("M月d日"),
                Text = info.Text,
                Icon = info.Icon,
                TempMax = tMax[i].GetDouble(),
                TempMin = tMin[i].GetDouble(),
                Wind = $"{windDirText}{windLevel}",
                PrecipitationProbability = precip[i].ValueKind == JsonValueKind.Number ? precip[i].GetInt32() : 0,
            });
        }

        // 实时部分
        int? humidity = null;
        string? currentWind = null;
        if (root.TryGetProperty("current", out var current))
        {
            if (current.TryGetProperty("relative_humidity_2m", out var h) && h.ValueKind == JsonValueKind.Number)
            {
                humidity = h.GetInt32();
            }

            if (current.TryGetProperty("wind_direction_10m", out var wd) &&
                current.TryGetProperty("wind_speed_10m", out var ws))
            {
                currentWind = $"{WeatherCode.WindDirection(wd.GetDouble())}{WeatherCode.WindLevel(ws.GetDouble())}";
            }

            // 实时温度与天气码覆盖"今天"这一格，保证现在=此刻
            if (current.TryGetProperty("temperature_2m", out var t) && t.ValueKind == JsonValueKind.Number)
            {
                var todayIndex = days.FindIndex(d => d.Date == today);
                if (todayIndex >= 0)
                {
                    var todayInfo = days[todayIndex];
                    days[todayIndex] = todayInfo with
                    {
                        TempMax = Math.Max(todayInfo.TempMax, t.GetDouble()),
                        Text = WeatherCode.Describe(current.TryGetProperty("weather_code", out var c) ? c.GetInt32() : 0).Text,
                    };
                }
            }
        }

        return new WeatherReport
        {
            CityName = cityName,
            Latitude = lat,
            Longitude = lon,
            Humidity = humidity,
            CurrentWind = currentWind,
            Days = days,
        };
    }

    private static async Task<(int? Index, string? Text)?> FetchAirQualityAsync(
        HttpClient http, double lat, double lon, CancellationToken cancellation)
    {
        try
        {
            var url = string.Format(
                AirQualityUrl,
                lat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                lon.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var response = await http.GetAsync(url, cancellation);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellation);
            using var doc = JsonDocument.Parse(json);
            var aqi = doc.RootElement.GetProperty("current").GetProperty("european_aqi").GetInt32();

            // european_aqi 0-100；这里按国内习惯映射成"优/良/轻度污染…"
            var text = aqi switch
            {
                <= 20 => "优",
                <= 40 => "良",
                <= 60 => "中等",
                <= 80 => "较差",
                <= 100 => "差",
                _ => "很差",
            };

            return (aqi, text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string WeekdayName(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };
}
