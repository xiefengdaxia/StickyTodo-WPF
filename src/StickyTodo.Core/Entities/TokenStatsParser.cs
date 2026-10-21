using System.Text.Json;

namespace StickyTodo.Core.Entities;

/// <summary>
/// 把 Token 面板接口返回的 JSON 转成模型。
/// 放在 Core 是为了能单测：这里只做纯解析，不发请求。
/// </summary>
public static class TokenStatsParser
{
    public static TokenStats Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var granularity = root.TryGetProperty("gran", out var gran) ? gran.GetString() ?? "hour" : "hour";

        var from = DateTimeOffset.MinValue;
        var to = DateTimeOffset.MinValue;
        if (root.TryGetProperty("range", out var range))
        {
            from = ReadUnixMs(range, "from");
            to = ReadUnixMs(range, "to");
        }

        var total = root.TryGetProperty("tokens", out var tokens) ? ReadUsage(tokens) : TokenUsage.Empty;
        var steps = root.TryGetProperty("steps", out var stepsElement) ? stepsElement.GetInt64() : 0;
        var activeDays = root.TryGetProperty("activeDays", out var daysElement) ? daysElement.GetInt32() : 0;

        var series = new List<TokenSeriesPoint>();
        if (root.TryGetProperty("series", out var seriesElement) && seriesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in seriesElement.EnumerateArray())
            {
                series.Add(new TokenSeriesPoint(
                    GetString(item, "key"),
                    GetString(item, "label"),
                    item.TryGetProperty("steps", out var s) ? s.GetInt64() : 0,
                    ReadUsage(item)));
            }
        }

        var models = new List<TokenModelUsage>();
        if (root.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelsElement.EnumerateArray())
            {
                var modelId = string.Empty;
                var providerId = string.Empty;

                if (item.TryGetProperty("model", out var model))
                {
                    modelId = GetString(model, "id");
                    providerId = GetString(model, "providerID");
                }

                models.Add(new TokenModelUsage(
                    modelId,
                    providerId,
                    item.TryGetProperty("steps", out var s) ? s.GetInt64() : 0,
                    item.TryGetProperty("tokens", out var t) ? ReadUsage(t) : TokenUsage.Empty));
            }
        }

        return new TokenStats(from, to, granularity, total, steps, activeDays, series, models);
    }

    private static TokenUsage ReadUsage(JsonElement element)
    {
        long cacheRead;
        long cacheWrite;

        if (element.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
        {
            cacheRead = cache.TryGetProperty("read", out var r) ? r.GetInt64() : 0;
            cacheWrite = cache.TryGetProperty("write", out var w) ? w.GetInt64() : 0;
        }
        else
        {
            // series 里是扁平的 cacheRead / cacheWrite
            cacheRead = element.TryGetProperty("cacheRead", out var r2) ? r2.GetInt64() : 0;
            cacheWrite = element.TryGetProperty("cacheWrite", out var w2) ? w2.GetInt64() : 0;
        }

        return new TokenUsage(
            element.TryGetProperty("input", out var i) ? i.GetInt64() : 0,
            element.TryGetProperty("output", out var o) ? o.GetInt64() : 0,
            element.TryGetProperty("reasoning", out var re) ? re.GetInt64() : 0,
            cacheRead,
            cacheWrite);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset ReadUnixMs(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return DateTimeOffset.MinValue;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(value.GetInt64()).ToLocalTime();
    }
}