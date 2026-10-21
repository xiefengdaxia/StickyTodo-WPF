using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Services;

/// <summary>
/// 回归演示用的假数据源：不联网，按小时造一天的量，形状和真实面板一致。
/// 只给 <c>--token-demo</c> 用，正式运行走 <see cref="HttpTokenStatsProvider"/>。
/// </summary>
public sealed class DemoTokenStatsProvider : ITokenStatsProvider
{
    private static readonly long[] HourlyInput =
    [
        120, 0, 0, 0, 0, 0, 0, 0, 3400, 12500, 8800, 6400,
        15200, 9100, 4300, 0, 7600, 21400, 16800, 5200, 0, 0, 0, 0,
    ];

    private static readonly long[] HourlyOutput =
    [
        8, 0, 0, 0, 0, 0, 0, 0, 210, 640, 430, 320,
        780, 520, 240, 0, 390, 1180, 860, 260, 0, 0, 0, 0,
    ];

    /// <summary>每次调用记一笔，回归里断言"点范围/间隔后真的重新取数了"。</summary>
    public List<(int Days, string Granularity)> Calls { get; } = new();

    public Task<TokenStats> FetchAsync(int days, string granularity, CancellationToken cancellationToken = default)
    {
        Calls.Add((days, granularity));

        var today = DateTimeOffset.Now.Date;
        var series = new List<TokenSeriesPoint>();

        if (granularity == "day")
        {
            for (var d = Math.Max(1, days) - 1; d >= 0; d--)
            {
                var date = today.AddDays(-d);
                var input = 42000 + (d * 3700);
                series.Add(new TokenSeriesPoint(
                    date.ToString("MM-dd"),
                    date.ToString("MM-dd"),
                    18 + d,
                    new TokenUsage(input, input / 20, input / 60, input / 2, input / 40)));
            }
        }
        else
        {
            for (var h = 0; h < 24; h++)
            {
                series.Add(new TokenSeriesPoint(
                    $"{today:MM-dd} {h:00}:00",
                    $"{h:00}:00",
                    HourlyInput[h] / 900,
                    new TokenUsage(
                        HourlyInput[h],
                        HourlyOutput[h],
                        HourlyOutput[h] / 4,
                        HourlyInput[h] / 2,
                        HourlyInput[h] / 40)));
            }
        }

        var total = TokenUsage.Empty;
        long steps = 0;
        foreach (var point in series)
        {
            total = TokenUsage.Add(total, point.Usage);
            steps += point.Steps;
        }

        var models = new List<TokenModelUsage>
        {
            new("deepseek-v4.1-flash", "magpie", steps, total),
            new("claude-sonnet-4", "anthropic", steps / 4, new TokenUsage(total.Input / 5, total.Output / 3, 0, total.CacheRead / 5, 0)),
        };

        var stats = new TokenStats(
            today,
            DateTimeOffset.Now,
            granularity,
            total,
            steps,
            Math.Max(1, Math.Min(days, 30)),
            series,
            models);

        return Task.FromResult(stats);
    }
}