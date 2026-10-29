using System.Text;
using System.Text.Json;

namespace StickyTodo.Weather;

/// <summary>一条搜索建议：输入框下拉里的候选项。</summary>
public sealed record DivisionSuggestion(string Name, string Province, string City, bool IsCity)
{
    /// <summary>下拉里显示的文字：市只带省，区县带上所属市。</summary>
    public string Label => IsCity ? $"{Name}  {Province}" : $"{Name}  {City}";

    /// <summary>无障碍/调试时也用同一行文字（UIA 的项名来自 ToString）。</summary>
    public override string ToString() => Label;
}

/// <summary>一条区县归属：省 / 地级市 / 区县。</summary>
public sealed record DivisionPlace(string Province, string City, string County)
{
    /// <summary>
    /// 真正用来查天气的城市名：
    /// 直辖市（北京/上海/天津/重庆）的"市"一级在数据里是「市辖区」，要用省名；
    /// 省直辖的县级行政区（如济源市）没有可用的上级市，为 null。
    /// </summary>
    public string? WeatherCity { get; init; }
}

/// <summary>
/// 全国省 / 市 / 区县表（离线，嵌入在 dll 里）。
///
/// 数据来源：GitHub 上的 <c>modood/Administrative-divisions-of-China</c>
/// （<c>dist/pca-code.json</c>），只保留三级名字、丢掉编码后存成
/// <c>Data/china-divisions.json</c>。
///
/// 为什么需要它：Open-Meteo 对中文区县一级的地名很不可靠——「岳麓」「炎陵」「娄底」
/// 直接查不到（响应里没有 results），「双峰」会解析到浙江金华。而天气本身是区域性的，
/// 区县查不到时按所属地级市查完全够用，所以先用这张表把区县换算成地级市。
/// </summary>
public static class ChinaDivisions
{
    private static readonly Lazy<Table> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>表里一共有多少个区县（回归/诊断用）。</summary>
    public static int CountyCount => Data.Value.Count;

    /// <summary>嵌入资源缺失或解析失败时为 false（此时调用方回落到在线地理编码）。</summary>
    public static bool IsAvailable => Data.Value.Count > 0;

    /// <summary>同名区县的所有归属（按行政区划代码顺序；直辖市在前）。</summary>
    public static IReadOnlyList<DivisionPlace> Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var key = name.Trim();
        var table = Data.Value;

        if (table.Exact.TryGetValue(key, out var exact))
        {
            return exact;
        }

        var loose = Normalize(key);
        if (loose.Length > 0 && table.Loose.TryGetValue(loose, out var matches))
        {
            return matches;
        }

        return [];
    }

    /// <summary>区县（含县级市）→ 所属地级市；查不到返回 false。</summary>
    public static bool TryGetParentCity(string name, out string city)
    {
        city = string.Empty;

        foreach (var place in Find(name))
        {
            if (place.WeatherCity is { } resolved)
            {
                city = resolved;
                return true;
            }
        }

        return false;
    }

    /// <summary>名字是不是这张表里出现过的省 / 市 / 区县（任意一级）。</summary>
    public static bool IsKnown(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var key = name.Trim();
        var table = Data.Value;

        return table.Exact.ContainsKey(key)
               || table.Cities.ContainsKey(key)
               || table.Provinces.Contains(key)
               || table.Loose.ContainsKey(Normalize(key));
    }

    /// <summary>
    /// 模糊搜索（输入框下拉用）：名字包含关键字的市 / 区县，最多 <paramref name="limit"/> 条。
    /// 排序：完全匹配 &gt; 前缀匹配 &gt; 包含匹配；同档里市在区县之前，短的在前。
    /// </summary>
    public static IReadOnlyList<DivisionSuggestion> Search(string query, int limit = 8)
    {
        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length == 0 || limit <= 0)
        {
            return [];
        }

        var table = Data.Value;
        var found = new List<(int Rank, string Name, DivisionSuggestion Item)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Consider(string name, string province, string city, bool isCity)
        {
            // 同名同市只留一条（数据里偶有重复行）
            if (!seen.Add(name + "|" + city))
            {
                return;
            }

            var rank = name == keyword ? 0 : name.StartsWith(keyword, StringComparison.Ordinal) ? 1 : 2;
            found.Add((rank * 2 + (isCity ? 0 : 1), name, new DivisionSuggestion(name, province, city, isCity)));
        }

        foreach (var (city, province) in table.Cities)
        {
            if (city.Contains(keyword, StringComparison.Ordinal))
            {
                Consider(city, province, city, isCity: true);
            }
        }

        foreach (var (county, places) in table.Exact)
        {
            if (!county.Contains(keyword, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var place in places)
            {
                Consider(county, place.Province, place.City, isCity: false);
            }
        }

        return found
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Name.Length)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Item)
            .ToList();
    }

    // ---------- 实现 ----------

    private sealed class Table
    {
        public int Count;

        /// <summary>区县全名 → 归属（同名区县会有多条）。</summary>
        public Dictionary<string, List<DivisionPlace>> Exact { get; } = new(StringComparer.Ordinal);

        /// <summary>去掉"市/县/区/旗"等后缀后的区县名 → 归属。</summary>
        public Dictionary<string, List<DivisionPlace>> Loose { get; } = new(StringComparer.Ordinal);

        /// <summary>地级市 → 省（下拉搜索用）。</summary>
        public Dictionary<string, string> Cities { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Provinces { get; } = new(StringComparer.Ordinal);
    }

    private static readonly string[] Suffixes =
    [
        "特别行政区", "自治区", "自治州", "自治县", "地区", "新区", "林区", "矿区",
        "市", "县", "区", "旗", "盟", "州",
    ];

    private static string Normalize(string name)
    {
        foreach (var suffix in Suffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    private static bool IsMunicipality(string province) =>
        province is "北京市" or "上海市" or "天津市" or "重庆市";

    private static bool IsPseudoCity(string city) =>
        city is "" or "市辖区" or "县" or "省直辖县级行政区划" or "自治区直辖县级行政区划";

    private static Table Load()
    {
        var table = new Table();

        try
        {
            var assembly = typeof(ChinaDivisions).Assembly;
            var name = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith("china-divisions.json", StringComparison.OrdinalIgnoreCase));

            if (name is null)
            {
                return table;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return table;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());

            foreach (var province in doc.RootElement.EnumerateArray())
            {
                var provinceName = province.GetProperty("name").GetString() ?? string.Empty;
                if (provinceName.Length == 0)
                {
                    continue;
                }

                table.Provinces.Add(provinceName);

                if (!province.TryGetProperty("children", out var cities))
                {
                    continue;
                }

                foreach (var city in cities.EnumerateArray())
                {
                    var cityName = city.GetProperty("name").GetString() ?? string.Empty;
                    if (cityName.Length > 0)
                    {
                        table.Cities[cityName] = provinceName;
                    }

                    if (!city.TryGetProperty("children", out var counties))
                    {
                        continue;
                    }

                    var weatherCity = IsPseudoCity(cityName)
                        ? (IsMunicipality(provinceName) ? provinceName : null)
                        : cityName;

                    foreach (var county in counties.EnumerateArray())
                    {
                        var countyName = county.GetProperty("name").GetString() ?? string.Empty;
                        if (countyName.Length == 0)
                        {
                            continue;
                        }

                        var place = new DivisionPlace(provinceName, cityName, countyName)
                        {
                            WeatherCity = weatherCity,
                        };

                        Add(table.Exact, countyName, place);
                        table.Count++;

                        var loose = Normalize(countyName);
                        if (loose.Length > 0 && !string.Equals(loose, countyName, StringComparison.Ordinal))
                        {
                            Add(table.Loose, loose, place);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 表读不出来就当没有：调用方会回落到在线地理编码，天气功能不受影响
            return new Table();
        }

        return table;
    }

    private static void Add(Dictionary<string, List<DivisionPlace>> map, string key, DivisionPlace place)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(place);
    }
}
