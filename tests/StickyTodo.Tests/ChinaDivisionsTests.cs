using StickyTodo.Weather;

namespace StickyTodo.Tests;

/// <summary>
/// 全国省/市/区县表 + 「区县查不到就按所属市查」的离线解析。
/// 这张表是嵌入资源，读不出来时天气会回落到在线地理编码——所以这里既验证
/// 数据本身，也验证解析规则（同名区县、直辖市、省直辖县级市）。
/// </summary>
public sealed class ChinaDivisionsTests
{
    [Fact]
    public void Dataset_IsEmbedded_AndCoversTheWholeCountry()
    {
        Assert.True(ChinaDivisions.IsAvailable);

        // 全国区县 2800+；低于 2000 说明资源没打进去或解析跑偏了
        Assert.True(ChinaDivisions.CountyCount > 2000, $"区县数量异常：{ChinaDivisions.CountyCount}");
    }

    [Theory]
    [InlineData("岳麓区", "长沙市")]
    [InlineData("天心区", "长沙市")]
    [InlineData("双峰县", "娄底市")]
    [InlineData("浏阳市", "长沙市")]   // 县级市
    [InlineData("义乌市", "金华市")]   // 县级市
    [InlineData("昆山市", "苏州市")]
    public void County_ResolvesToItsPrefectureCity(string county, string city)
    {
        Assert.True(ChinaDivisions.TryGetParentCity(county, out var actual));
        Assert.Equal(city, actual);
    }

    [Theory]
    [InlineData("浦东新区", "上海市")]
    [InlineData("东城区", "北京市")]
    [InlineData("朝阳区", "北京市")]   // 同名：北京 / 长春，按行政区划代码顺序取北京
    public void MunicipalityDistrict_FallsBackToCityName(string district, string city)
    {
        Assert.True(ChinaDivisions.TryGetParentCity(district, out var actual));
        Assert.Equal(city, actual);
    }

    [Theory]
    [InlineData("长安区")]   // 石家庄 / 西安，第一个能定位到就行
    [InlineData("鼓楼区")]   // 南京 / 徐州 / 福州 / 开封
    public void AmbiguousDistrict_StillResolves(string district)
    {
        Assert.True(ChinaDivisions.TryGetParentCity(district, out var city));
        Assert.False(string.IsNullOrEmpty(city));
    }

    [Fact]
    public void ProvinceAdministeredCounty_HasNoParentCity()
    {
        // 济源市直属河南省，没有地级市上级 → 交给在线地理编码
        Assert.False(ChinaDivisions.TryGetParentCity("济源市", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("不存在的区")]
    public void Unknown_ReturnsFalse(string name)
    {
        Assert.False(ChinaDivisions.TryGetParentCity(name, out _));
        Assert.Empty(ChinaDivisions.Find(name));
    }

    [Fact]
    public void Find_KeepsEveryAmbiguousHit()
    {
        var places = ChinaDivisions.Find("朝阳区");

        Assert.True(places.Count >= 2);
        Assert.Contains(places, p => p.Province == "北京市");
        Assert.Contains(places, p => p.Province == "吉林省");
    }

    // ---------- 天气定位：区县 → 所属市 → 内置坐标 ----------

    [Theory]
    [InlineData("浏阳市", "长沙市")]   // 县级市：自身不在内置表，按所属市查
    [InlineData("昆山市", "苏州市")]
    [InlineData("义乌市", "金华市")]
    public void WeatherLookup_FallsBackToCityCoordinates(string county, string expectedCity)
    {
        Assert.True(WeatherService.TryResolveOffline(county, out var lat, out var lon, out var resolved));

        Assert.Equal(expectedCity, resolved);
        Assert.True(WeatherService.TryResolveKnown(expectedCity, out var cityLat, out var cityLon));
        Assert.Equal(cityLat, lat, 4);
        Assert.Equal(cityLon, lon, 4);
    }

    [Theory]
    [InlineData("岳麓区")]   // 内置表里有「岳麓」，去后缀就能命中，用区本身更准
    [InlineData("双峰县")]
    public void WeatherLookup_PrefersTheDistrictItself_WhenBuiltIn(string county)
    {
        Assert.True(WeatherService.TryResolveOffline(county, out _, out _, out var resolved));
        Assert.Equal(county, resolved);
    }

    [Fact]
    public void WeatherLookup_KeepsExactKnownCity()
    {
        Assert.True(WeatherService.TryResolveOffline("双峰", out var lat, out var lon, out var resolved));

        // 双峰本身在内置坐标表里，不该被换成娄底
        Assert.Equal("双峰", resolved);
        Assert.True(WeatherService.TryResolveKnown("双峰", out var expectedLat, out var expectedLon));
        Assert.Equal(expectedLat, lat, 4);
        Assert.Equal(expectedLon, lon, 4);
    }

    [Theory]
    [InlineData("不存在的城市名")]
    [InlineData("")]
    public void WeatherLookup_UnknownCity_IsNotResolvedOffline(string city)
    {
        Assert.False(WeatherService.TryResolveOffline(city, out _, out _, out _));
    }

    // ---------- 输入框的模糊搜索 ----------

    [Fact]
    public void Search_FindsCitiesAndCounties()
    {
        var results = ChinaDivisions.Search("长沙");

        Assert.NotEmpty(results);
        Assert.Equal("长沙市", results[0].Name);   // 完全匹配的市排第一
        Assert.True(results[0].IsCity);
        Assert.Contains(results, r => r.Name == "长沙县");
    }

    [Fact]
    public void Search_MatchesPartialName()
    {
        Assert.Contains(ChinaDivisions.Search("浏阳"), r => r.Name == "浏阳市");
        Assert.Contains(ChinaDivisions.Search("岳麓"), r => r.Name == "岳麓区");
    }

    [Fact]
    public void Search_PrefersCityOverCounty_OnSameMatch()
    {
        var results = ChinaDivisions.Search("长沙").ToList();
        var cityIndex = results.FindIndex(r => r.IsCity);
        var countyIndex = results.FindIndex(r => !r.IsCity);

        Assert.True(cityIndex >= 0 && countyIndex >= 0);
        Assert.True(cityIndex < countyIndex);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsNothing()
    {
        Assert.Empty(ChinaDivisions.Search(""));
        Assert.Empty(ChinaDivisions.Search("   "));
        Assert.Empty(ChinaDivisions.Search("长沙", limit: 0));
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var results = ChinaDivisions.Search("市", limit: 5);
        Assert.True(results.Count <= 5);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Search_Label_ShowsProvinceForCity_AndParentCityForCounty()
    {
        var city = ChinaDivisions.Search("长沙市").First(r => r.IsCity);
        Assert.Contains("湖南", city.Label);

        var county = ChinaDivisions.Search("浏阳市").First(r => !r.IsCity);
        Assert.Contains("长沙", county.Label);
    }
}
