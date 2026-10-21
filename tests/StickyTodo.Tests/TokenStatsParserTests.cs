using StickyTodo.Core.Entities;

namespace StickyTodo.Tests;

/// <summary>
/// Token 面板接口的解析测试。用一段和真实返回同构的 JSON，
/// 覆盖汇总、趋势点、按模型三层，以及字段缺失时的兜底。
/// </summary>
public sealed class TokenStatsParserTests
{
    private const string SampleJson = """
    {
      "range": { "from": 1792512000000, "to": 1792541011764, "tz": "Asia/Shanghai", "source": "db" },
      "gran": "hour",
      "tokens": {
        "input": 10331,
        "output": 39,
        "reasoning": 11,
        "cache": { "read": 4864, "write": 7 }
      },
      "steps": 2,
      "activeDays": 1,
      "series": [
        { "key": "10-21 07:00", "label": "07:00", "steps": 0, "input": 0, "output": 0, "reasoning": 0, "cacheRead": 0, "cacheWrite": 0 },
        { "key": "10-21 08:00", "label": "08:00", "steps": 2, "input": 10331, "output": 39, "reasoning": 11, "cacheRead": 4864, "cacheWrite": 7 }
      ],
      "models": [
        {
          "model": { "id": "deepseek-v4.1-flash", "providerID": "magpie" },
          "steps": 2,
          "tokens": { "input": 10331, "output": 39, "reasoning": 11, "cache": { "read": 4864, "write": 7 } }
        }
      ]
    }
    """;

    [Fact]
    public void Parse_ReadsTotals()
    {
        var stats = TokenStatsParser.Parse(SampleJson);

        Assert.Equal(10331, stats.Total.Input);
        Assert.Equal(39, stats.Total.Output);
        Assert.Equal(11, stats.Total.Reasoning);
        Assert.Equal(4864, stats.Total.CacheRead);
        Assert.Equal(7, stats.Total.CacheWrite);
        Assert.Equal(15252, stats.Total.Total);
        Assert.Equal(2, stats.Steps);
        Assert.Equal(1, stats.ActiveDays);
        Assert.Equal("hour", stats.Granularity);
    }

    [Fact]
    public void Parse_ComputesCacheRatio()
    {
        var stats = TokenStatsParser.Parse(SampleJson);

        // 4864 / (4864 + 10331)
        Assert.Equal(4864d / 15195d, stats.Total.CacheRatio, 6);
    }

    [Fact]
    public void Parse_ReadsSeriesWithFlatCacheFields()
    {
        var stats = TokenStatsParser.Parse(SampleJson);

        Assert.Equal(2, stats.Series.Count);
        Assert.Equal("10-21 08:00", stats.Series[1].Key);
        Assert.Equal("08:00", stats.Series[1].Label);
        Assert.Equal(4864, stats.Series[1].Usage.CacheRead);
        Assert.Equal(7, stats.Series[1].Usage.CacheWrite);
        Assert.Equal(15252, stats.Series[1].Total);
        Assert.Equal(0, stats.Series[0].Total);
    }

    [Fact]
    public void Parse_ReadsModels()
    {
        var stats = TokenStatsParser.Parse(SampleJson);

        var model = Assert.Single(stats.Models);
        Assert.Equal("deepseek-v4.1-flash", model.ModelId);
        Assert.Equal("magpie", model.ProviderId);
        Assert.Equal("deepseek-v4.1-flash · magpie", model.DisplayName);
        Assert.Equal(2, model.Steps);
        Assert.Equal(15252, model.Total);
    }

    [Fact]
    public void Parse_ReadsRangeAsLocalTime()
    {
        var stats = TokenStatsParser.Parse(SampleJson);

        Assert.NotEqual(DateTimeOffset.MinValue, stats.From);
        Assert.True(stats.To > stats.From);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792512000000).ToLocalTime(), stats.From);
    }

    [Fact]
    public void Parse_ToleratesMissingSections()
    {
        var stats = TokenStatsParser.Parse("""{ "gran": "day" }""");

        Assert.Equal("day", stats.Granularity);
        Assert.Equal(0, stats.Total.Total);
        Assert.Empty(stats.Series);
        Assert.Empty(stats.Models);
        Assert.Equal(DateTimeOffset.MinValue, stats.From);
    }

    [Fact]
    public void Parse_EmptyRangeGivesZeroCacheRatio()
    {
        var usage = TokenUsage.Empty;

        Assert.Equal(0, usage.CacheRatio);
        Assert.Equal(0, usage.Total);
    }
}