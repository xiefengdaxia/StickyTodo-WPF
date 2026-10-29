using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using StickyTodo.Infrastructure.Token.Agents;

namespace StickyTodo.Infrastructure.Token;

/// <summary>
/// 多 Agent 聚合统计：把 opencode / zcode / codex / workbuddy / claude / gemini /
/// qoder / kimi / pi 各自的本地账本合成一个 <see cref="ITokenStatsProvider"/>。
///
/// <para><b>口径</b>：每个源是各自客户端的独立账本，同一次调用不会出现在两个源里，
/// 所以"全部"= 各源求和，不需要去重。</para>
///
/// <para><b>agentFilter</b>：null = 全部；"zcode" 等 = 只统计该客户端。
/// 选中客户端后趋势/汇总/按模型/复制摘要全部按它重新统计。</para>
/// </summary>
public sealed class MultiAgentTokenStatsProvider : ITokenStatsProvider
{
    private readonly IAgentTokenSource[] _sources;
    private readonly ITokenStatsProvider _fallback;

    /// <summary>兼容旧的构造（只挂 opencode）。生产代码请用 <see cref="CreateDefault"/>。</summary>
    public MultiAgentTokenStatsProvider(ITokenStatsProvider fallback, params IAgentTokenSource[] sources)
    {
        _fallback = fallback;
        _sources = sources;
    }

    /// <summary>
    /// 数据源清单。手写而非反射扫描：多一个客户端时显式加一行，
    /// 也方便在测试里注入假源。
    /// </summary>
    public static MultiAgentTokenStatsProvider CreateDefault(string? opencodeDatabasePath = null)
    {
        return new MultiAgentTokenStatsProvider(
            new OpenCodeTokenStatsProvider(opencodeDatabasePath),
            new OpenCodeAgentTokenSource(opencodeDatabasePath),
            new ZcodeAgentSource(),
            new CodexAgentSource(),
            new WorkbuddyAgentSource(),
            new ClaudeAgentSource(),
            new GeminiAgentSource(),
            new QoderAgentSource(),
            new KimiAgentSource(),
            new PiAgentSource());
    }

    /// <summary>全部注册的客户端（含不可用的——设置页要能展示"未检测到"）。</summary>
    public IReadOnlyList<IAgentTokenSource> Sources => _sources;

    /// <summary>当前筛选的 agent（null = 全部）。供 UI 回显与缓存键使用。</summary>
    public string? AgentFilter { get; private set; }

    public string SourceDescription
    {
        get
        {
            var available = _sources.Where(s => s.IsAvailable).ToList();
            if (available.Count == 0)
            {
                return "未检测到任何 Agent 客户端数据";
            }

            if (AgentFilter is { } filter)
            {
                var one = available.FirstOrDefault(s => s.AgentId == filter) ?? available[0];
                return one.SourceDescription;
            }

            return $"本机 {available.Count} 个客户端 · {string.Join(" + ", available.Select(s => s.DisplayName))}";
        }
    }

    public bool IsAvailable => _sources.Any(s => s.IsAvailable);

    /// <summary>切换统计的客户端（null = 全部）。UI 的下拉框选中项变化时调用。</summary>
    public void SetAgentFilter(string? agentId)
    {
        AgentFilter = string.IsNullOrWhiteSpace(agentId) || agentId == "all" ? null : agentId;
    }

    public async Task<TokenStats> FetchAsync(int days, string granularity, CancellationToken cancellationToken = default)
    {
        var active = _sources
            .Where(s => s.IsAvailable && (AgentFilter is null || s.AgentId == AgentFilter))
            .ToList();

        // 一个可用源都没有：走兼容 fallback（保留旧行为——比如设置里把 opencode
        // 指到别的路径的场景），否则返回空
        if (active.Count == 0)
        {
            return _fallback.IsAvailable
                ? await _fallback.FetchAsync(days, granularity, cancellationToken).ConfigureAwait(false)
                : TokenStats.Empty;
        }

        var fromDay = days <= 0
            ? DateOnly.FromDateTime(DateTime.Now)
            : DateOnly.FromDateTime(DateTime.Now).AddDays(-(days - 1));

        // 各源并行读（每个源自己有按天缓存），UI 线程外调用
        var tasks = active.Select(s => Task.Run(
                () => s.ReadEntries(fromDay).ToList(),
                cancellationToken))
            .ToList();

        var all = new List<AgentUsageEntry>();
        foreach (var task in tasks)
        {
            all.AddRange(await task.ConfigureAwait(false));
        }

        return BuildStats(all, fromDay, granularity);
    }

    private static TokenStats BuildStats(List<AgentUsageEntry> entries, DateOnly fromDay, string granularity)
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var isHourly = granularity != "day";

        var total = TokenUsage.Empty;
        long steps = 0;
        foreach (var entry in entries)
        {
            total = TokenUsage.Add(total, entry.Usage);
            steps += entry.Steps;
        }

        var models = entries
            .GroupBy(e => (e.ModelId, e.ProviderId))
            .Select(g => new TokenModelUsage(
                g.Key.ModelId,
                g.Key.ProviderId,
                g.Sum(e => e.Steps),
                g.Aggregate(TokenUsage.Empty, (acc, e) => TokenUsage.Add(acc, e.Usage))))
            .OrderByDescending(m => m.Total)
            .ToList();

        var series = new List<TokenSeriesPoint>();

        if (isHourly)
        {
            // 小时视图：按小时合并（跨天时同小时累加），键带上日期避免 30 天视图撞键
            var byHour = entries
                .GroupBy(e => e.Timestamp.Hour)
                .ToDictionary(
                    g => g.Key,
                    g => (Steps: g.Sum(e => e.Steps), Usage: g.Aggregate(TokenUsage.Empty, (a, e) => TokenUsage.Add(a, e.Usage))));

            for (var hour = 0; hour < 24; hour++)
            {
                var has = byHour.TryGetValue(hour, out var value);
                series.Add(new TokenSeriesPoint(
                    $"{fromDay:MM-dd} {hour:00}:00",
                    $"{hour:00}:00",
                    has ? value.Steps : 0,
                    has ? value.Usage : TokenUsage.Empty));
            }
        }
        else
        {
            var byDay = entries
                .GroupBy(e => DateOnly.FromDateTime(e.Timestamp.LocalDateTime))
                .ToDictionary(
                    g => g.Key,
                    g => (Steps: g.Sum(e => e.Steps), Usage: g.Aggregate(TokenUsage.Empty, (a, e) => TokenUsage.Add(a, e.Usage))));

            for (var day = fromDay; day <= today; day = day.AddDays(1))
            {
                var has = byDay.TryGetValue(day, out var value);
                series.Add(new TokenSeriesPoint(
                    day.ToString("yyyy-MM-dd"),
                    day.ToString("MM-dd"),
                    has ? value.Steps : 0,
                    has ? value.Usage : TokenUsage.Empty));
            }
        }

        var activeDays = series
            .Where(p => p.Total > 0)
            .Select(p => p.Key.Length >= 10 ? p.Key[..10] : p.Key)
            .Distinct()
            .Count();

        return new TokenStats(
            new DateTimeOffset(fromDay.ToDateTime(TimeOnly.MinValue)),
            now,
            granularity,
            total,
            steps,
            activeDays,
            series,
            models);
    }
}
