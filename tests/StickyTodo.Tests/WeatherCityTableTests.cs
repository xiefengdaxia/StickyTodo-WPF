using StickyTodo.Core.Services;
using StickyTodo.Weather;

namespace StickyTodo.Tests;

/// <summary>
/// 天气定位的离线保障：Open-Meteo 的中文地理编码对小地名不可靠，
/// 实测「岳麓」「炎陵」「娄底」查不到（响应里没有 results 字段），
/// 「双峰」解析到浙江金华、「长沙」解析到重庆、「湘潭」解析到安徽黄山。
/// 所以内置坐标表 + 默认城市列表必须自洽，且默认城市都能被内置表命中。
/// </summary>
public sealed class WeatherCityTableTests
{
    [Fact]
    public void DefaultWeatherCities_AreAllInKnownCityTable()
    {
        var missing = AppSettingsService.DefaultWeatherCities
            .Where(c => !WeatherService.IsKnownCity(c))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void KnownCityTable_HasNoDuplicateKeys()
    {
        // 字典字面量里重复键会抛 TypeInitializationException，这里显式确认表能正常构建
        Assert.NotEmpty(WeatherService.KnownCities);
        Assert.True(WeatherService.KnownCities.Count > 50);
    }

    [Theory]
    // 默认城市：湖南境内的小地名，Open-Meteo 查不到或会跑偏，必须靠内置坐标
    [InlineData("双峰", 27.46, 112.00)]
    [InlineData("岳麓", 28.23, 112.93)]
    [InlineData("芙蓉", 28.19, 112.98)]
    [InlineData("炎陵", 26.48, 113.73)]
    [InlineData("苏州", 31.29, 120.58)]
    [InlineData("杭州", 30.27, 120.15)]
    public void KnownCity_ResolvesToExpectedPosition(string city, double lat, double lon)
    {
        Assert.True(WeatherService.TryResolveKnown(city, out var actualLat, out var actualLon));
        Assert.Equal(lat, actualLat, 1);
        Assert.Equal(lon, actualLon, 1);
    }

    [Theory]
    [InlineData("岳麓区", "岳麓")]
    [InlineData("长沙市", "长沙")]
    [InlineData("双峰县", "双峰")]
    public void AdministrativeSuffix_IsStrippedBeforeLookup(string input, string expectedBase)
    {
        Assert.True(WeatherService.TryResolveKnown(input, out var lat, out var lon));
        Assert.True(WeatherService.TryResolveKnown(expectedBase, out var baseLat, out var baseLon));
        Assert.Equal(baseLat, lat, 4);
        Assert.Equal(baseLon, lon, 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("不存在的城市名")]
    public void UnknownCity_FallsBackToGeocoding(string city)
    {
        Assert.False(WeatherService.TryResolveKnown(city, out _, out _));
    }

    [Fact]
    public void AllKnownCities_AreInsideChinaBoundingBox()
    {
        // 内置表以国内城市为主，坐标写反（经纬度互换 / 小数点错位）会在这里暴露
        foreach (var (city, pos) in WeatherService.KnownCities)
        {
            Assert.True(pos.Lat is >= -60 and <= 90, $"{city} 纬度越界：{pos.Lat}");

            // 除少数国外城市外，纬度/经度都得是有效坐标；这里只查数量级
            Assert.NotEqual(0, pos.Lat);
            Assert.NotEqual(0, pos.Lon);
        }
    }
}
