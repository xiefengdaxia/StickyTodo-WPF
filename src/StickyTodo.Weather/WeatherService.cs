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

/// <summary>一次天气抓取的结果：成功带报告，失败带可读原因。</summary>
public sealed record WeatherFetchOutcome
{
    private WeatherFetchOutcome(WeatherReport? report, string? error)
    {
        Report = report;
        Error = error;
    }

    public WeatherReport? Report { get; }

    /// <summary>失败原因（用户可读）；成功时为 null。</summary>
    public string? Error { get; }

    public bool Success => Report is not null;

    public static WeatherFetchOutcome Ok(WeatherReport report) => new(report, null);

    public static WeatherFetchOutcome Fail(string error) => new(null, error);
}

/// <summary>
/// 天气数据访问：Open-Meteo 免费接口（无需注册、无 API key）。
///   地理编码: https://geocoding-api.open-meteo.com/v1/search?name=双峰&language=zh&count=1
///   天气:     https://api.open-meteo.com/v1/forecast?...&daily=...&current=...
///   空气质量: https://air-quality-api.open-meteo.com/v1/air-quality?...&hourly=...（ european_aqi 为整数）
///
/// 这是全应用唯一的联网功能，超时 8 秒；失败带原因文本，界面显示"获取失败：原因"。
///
/// 中文小地名在 Open-Meteo 的地理库里很不可靠：实测「岳麓」「炎陵」「娄底」根本查不到
/// （返回的 JSON 没有 results 字段），「双峰」会解析到浙江金华、「长沙」解析到重庆、
/// 「湘潭」解析到安徽黄山。所以先查内置坐标表，查不到才回落到在线地理编码。
/// </summary>
public sealed class WeatherService
{
    /// <summary>内置城市坐标表：城市名 → (纬度, 经度)。WGS84，与 Open-Meteo 接口一致。</summary>
    public static readonly IReadOnlyDictionary<string, (double Lat, double Lon)> KnownCities =
        new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase)
        {
            // 湖南（默认城市）
            ["长沙"] = (28.2278, 112.9388),
            ["岳麓"] = (28.2300, 112.9330),
            ["芙蓉"] = (28.1945, 112.9867),
            ["天心"] = (28.1170, 112.9730),
            ["开福"] = (28.2530, 112.9730),
            ["望城"] = (28.3520, 112.8190),
            ["双峰"] = (27.4608, 112.0020),
            ["娄底"] = (27.7340, 111.9950),
            ["炎陵"] = (26.4870, 113.7310),
            ["株洲"] = (27.8270, 113.1340),
            ["湘潭"] = (27.8300, 112.9400),
            ["衡阳"] = (26.8930, 112.5720),
            ["岳阳"] = (29.3570, 113.1290),
            ["常德"] = (29.0400, 111.6830),
            ["郴州"] = (25.7700, 113.0160),
            ["邵阳"] = (27.2400, 111.4650),
            ["张家界"] = (29.1170, 110.4790),
            ["益阳"] = (28.5530, 112.3550),
            ["永州"] = (26.4360, 111.6140),
            ["怀化"] = (27.5500, 110.0000),

            // 国内主要城市
            ["北京"] = (39.9042, 116.4074),
            ["上海"] = (31.2304, 121.4737),
            ["广州"] = (23.1291, 113.2644),
            ["深圳"] = (22.5431, 114.0579),
            ["杭州"] = (30.2741, 120.1551),
            ["苏州"] = (31.2989, 120.5853),
            ["南京"] = (32.0603, 118.7969),
            ["成都"] = (30.5728, 104.0668),
            ["武汉"] = (30.5928, 114.3055),
            ["西安"] = (34.3416, 108.9398),
            ["重庆"] = (29.5630, 106.5516),
            ["天津"] = (39.3434, 117.3616),
            ["郑州"] = (34.7466, 113.6254),
            ["济南"] = (36.6512, 117.1201),
            ["青岛"] = (36.0671, 120.3826),
            ["大连"] = (38.9140, 121.6147),
            ["沈阳"] = (41.8057, 123.4315),
            ["哈尔滨"] = (45.8038, 126.5340),
            ["长春"] = (43.8171, 125.3235),
            ["石家庄"] = (38.0428, 114.5149),
            ["太原"] = (37.8706, 112.5489),
            ["合肥"] = (31.8206, 117.2272),
            ["宁波"] = (29.8683, 121.5440),
            ["厦门"] = (24.4798, 118.0819),
            ["福州"] = (26.0745, 119.2965),
            ["南昌"] = (28.6820, 115.8579),
            ["南宁"] = (22.8170, 108.3665),
            ["昆明"] = (25.0389, 102.7183),
            ["贵阳"] = (26.6470, 106.6302),
            ["兰州"] = (36.0611, 103.8343),
            ["银川"] = (38.4872, 106.2309),
            ["乌鲁木齐"] = (43.8256, 87.6168),
            ["呼和浩特"] = (40.8424, 111.7490),
            ["拉萨"] = (29.6500, 91.1000),
            ["海口"] = (20.0444, 110.1999),
            ["三亚"] = (18.2528, 109.8539),
            ["东莞"] = (23.0207, 113.7518),
            ["佛山"] = (23.0218, 113.1219),
            ["珠海"] = (22.2707, 113.5767),
            ["无锡"] = (31.4912, 120.3119),
            ["常州"] = (31.8106, 119.9740),
            ["温州"] = (28.0000, 120.6720),
            ["金华"] = (29.0780, 119.6470),
            ["洛阳"] = (34.6180, 112.4540),
            ["徐州"] = (34.2058, 117.2840),
            ["烟台"] = (37.5330, 121.3800),
            ["绵阳"] = (31.4680, 104.6790),
            ["遵义"] = (27.7210, 106.9280),
            ["桂林"] = (25.2736, 110.2990),

            // 港澳台
            ["香港"] = (22.3193, 114.1694),
            ["澳门"] = (22.1987, 113.5439),
            ["台北"] = (25.0330, 121.5654),

            // 常用国外城市
            ["东京"] = (35.6762, 139.6503),
            ["首尔"] = (37.5665, 126.9780),
            ["新加坡"] = (1.3521, 103.8198),
            ["曼谷"] = (13.7563, 100.5018),
            ["纽约"] = (40.7128, -74.0060),
            ["洛杉矶"] = (34.0522, -118.2437),
            ["旧金山"] = (37.7749, -122.4194),
            ["芝加哥"] = (41.8781, -87.6298),
            ["温哥华"] = (49.2827, -123.1207),
            ["伦敦"] = (51.5074, -0.1278),
            ["巴黎"] = (48.8566, 2.3522),
            ["柏林"] = (52.5200, 13.4050),
            ["莫斯科"] = (55.7558, 37.6173),
            ["迪拜"] = (25.2048, 55.2708),
            ["悉尼"] = (-33.8688, 151.2093),
            ["墨尔本"] = (-37.8136, 144.9631),
        };

    /// <summary>城市名是否命中内置坐标表。</summary>
    public static bool IsKnownCity(string city) => KnownCities.ContainsKey(city);

    /// <summary>
    /// 离线解析地名 → 坐标：先查内置坐标表，查不到再查全国省市区县表，
    /// 把区县 / 县级市换算成所属地级市（天气本来就是区域性的）。
    /// <paramref name="resolvedName"/> 是实际查询用的地名——区县会变成市名。
    /// </summary>
    public static bool TryResolveOffline(string query, out double lat, out double lon, out string resolvedName)
    {
        lat = 0;
        lon = 0;
        resolvedName = (query ?? string.Empty).Trim();

        if (resolvedName.Length == 0)
        {
            return false;
        }

        if (TryResolveKnown(resolvedName, out lat, out lon))
        {
            return true;
        }

        // 同名区县（朝阳区：北京 / 长春）按行政区划代码顺序取，优先能直接定位的那个
        foreach (var place in ChinaDivisions.Find(resolvedName))
        {
            if (place.WeatherCity is { } city && TryResolveKnown(city, out lat, out lon))
            {
                resolvedName = city;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 用内置坐标表解析城市：先原名，再去掉常见行政后缀（"岳麓区"→"岳麓"）试一次。
    /// </summary>
    public static bool TryResolveKnown(string city, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;

        if (string.IsNullOrWhiteSpace(city))
        {
            return false;
        }

        if (KnownCities.TryGetValue(city.Trim(), out var hit))
        {
            (lat, lon) = hit;
            return true;
        }

        foreach (var suffix in new[] { "特别行政区", "自治州", "地区", "市", "县", "区", "州" })
        {
            if (city.Length > suffix.Length && city.EndsWith(suffix, StringComparison.Ordinal)
                && KnownCities.TryGetValue(city[..^suffix.Length], out hit))
            {
                (lat, lon) = hit;
                return true;
            }
        }

        return false;
    }

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

    /// <summary>抓一个城市的天气；失败时带上用户可读的原因。</summary>
    public async Task<WeatherFetchOutcome> FetchWithOutcomeAsync(string city, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return WeatherFetchOutcome.Fail("城市名为空");
        }

        city = city.Trim();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            double lat;
            double lon;

            // 1) 离线解析：内置坐标表 + 全国省市区县表（区县/县级市按所属地级市定位）
            if (TryResolveOffline(city, out lat, out lon, out var resolved))
            {
                var report = await FetchForecastAsync(http, lat, lon, resolved, cancellation);
                return await FinishAsync(http, report, resolved, cancellation);
            }

            // 2) 在线地理编码；区县查不到就退一步用所属地级市再查一次
            var geo = await GeocodeAsync(http, city, cancellation);
            var firstError = geo.Error;
            var name = city;

            // 中文地名只认国内结果：Open-Meteo 会把同名小地名解析到别的国家
            if (geo.Lat is not null && HasCjk(city) && !IsChina(geo.Country))
            {
                geo = (null, null, string.Empty, null, $"查不到「{city}」这个城市");
            }

            if (geo.Lat is null
                && ChinaDivisions.TryGetParentCity(city, out var parent)
                && !string.Equals(parent, city, StringComparison.Ordinal))
            {
                geo = await GeocodeAsync(http, parent, cancellation);
                name = parent;
            }

            if (geo.Lat is null)
            {
                return WeatherFetchOutcome.Fail(firstError ?? geo.Error ?? $"查不到「{city}」这个城市");
            }

            lat = geo.Lat.Value;
            lon = geo.Lon!.Value;
            name = geo.Name.Length > 0 ? geo.Name : name;

            var forecast = await FetchForecastAsync(http, lat, lon, name, cancellation);
            return await FinishAsync(http, forecast, name, cancellation);
        }
        catch (TaskCanceledException)
        {
            return WeatherFetchOutcome.Fail("请求超时");
        }
        catch (HttpRequestException ex)
        {
            return WeatherFetchOutcome.Fail("网络错误：" + ex.Message);
        }
        catch (Exception ex)
        {
            return WeatherFetchOutcome.Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task<WeatherFetchOutcome> FinishAsync(
        HttpClient http, WeatherReport? report, string name, CancellationToken cancellation)
    {
        if (report is null || report.Days.Count == 0)
        {
            // 接口通了但没给预报数据：明确说"暂无数据"，不要拿别的城市顶上
            return WeatherFetchOutcome.Fail($"「{name}」暂无天气数据");
        }

        // 空气质量是加分项，失败不影响主数据
        var aqi = await FetchAirQualityAsync(http, report.Latitude, report.Longitude, cancellation);

        return WeatherFetchOutcome.Ok(report with
        {
            AirQualityIndex = aqi?.Index,
            AirQualityText = aqi?.Text,
        });
    }

    private static async Task<(double? Lat, double? Lon, string Name, string? Country, string? Error)> GeocodeAsync(
        HttpClient http, string city, CancellationToken cancellation)
    {
        var url = string.Format(GeocodingUrl, Uri.EscapeDataString(city));
        using var response = await http.GetAsync(url, cancellation);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellation);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Open-Meteo 查不到时直接不返回 results 字段（而不是空数组），这里要判存在性
        if (!root.TryGetProperty("results", out var results))
        {
            return (null, null, string.Empty, null, $"查不到「{city}」这个城市");
        }

        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            return (null, null, string.Empty, null, $"查不到「{city}」这个城市");
        }

        var first = results[0];
        return (
            first.GetProperty("latitude").GetDouble(),
            first.GetProperty("longitude").GetDouble(),
            first.TryGetProperty("name", out var name) ? name.GetString() ?? city : city,
            first.TryGetProperty("country_code", out var code) ? code.GetString() : null,
            null);
    }

    /// <summary>查询词里有没有汉字（用来判断"中文地名只认国内结果"）。</summary>
    private static bool HasCjk(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '\u4E00' and <= '\u9FFF')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsChina(string? countryCode) =>
        string.Equals(countryCode, "CN", StringComparison.OrdinalIgnoreCase);

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
