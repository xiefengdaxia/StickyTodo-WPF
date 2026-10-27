using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using StickyTodo.Infrastructure.Token;

namespace StickyTodo.Tests;

/// <summary>
/// 多 Agent 聚合统计的口径测试（注入假源，不碰真实客户端数据）：
///   1. "全部" = 各源求和（各源是独立账本，不需要去重）；
///   2. 单选某个源时只统计它；
///   3. 不可用的源被跳过，"全部"的可用数正确；
///   4. 缓存跨 agent 隔离（看板层负责，这里验证 provider 层 SetAgentFilter 即时生效）。
/// </summary>
public sealed class MultiAgentTokenStatsProviderTests
{
    /// <summary>假源：固定返回几条记录。</summary>
    private sealed class FakeSource : IAgentTokenSource
    {
        private readonly List<AgentUsageEntry> _entries;

        public FakeSource(string agentId, string displayName, params AgentUsageEntry[] entries)
        {
            AgentId = agentId;
            DisplayName = displayName;
            _entries = [.. entries];
        }

        public bool Available { get; init; } = true;

        public string AgentId { get; }
        public string DisplayName { get; }
        public string SourceDescription => $"{DisplayName} · fake";
        public bool IsAvailable => Available;

        public IEnumerable<AgentUsageEntry> ReadEntries(DateOnly fromDay)
            => _entries.Where(e => DateOnly.FromDateTime(e.Timestamp.LocalDateTime) >= fromDay);
    }

    /// <summary>空 provider：聚合层所有源都不可用时才走到它，测试里占位。</summary>
    private sealed class EmptyProvider : ITokenStatsProvider
    {
        public string SourceDescription => "empty";
        public bool IsAvailable => false;
        public Task<TokenStats> FetchAsync(int days, string granularity, CancellationToken cancellationToken = default)
            => Task.FromResult(TokenStats.Empty);
    }

    private static AgentUsageEntry Entry(DateTimeOffset ts, string model, long input, long output)
        => new(ts, model, "fake-provider", new TokenUsage(input, output, 0, 0, 0));

    private static MultiAgentTokenStatsProvider CreateProvider(out FakeSource zcode, out FakeSource codex)
    {
        // 插入时间用"今天 00:00:01"：任意时刻运行都落在今天窗口内
        //（用 Now-N 小时会跨天——午夜后跑时"昨天"不在 days=0 的窗口里）
        var today = DateTimeOffset.Now.Date;
        var todayStart = today.AddSeconds(1);
        zcode = new FakeSource("zcode", "ZCode",
            Entry(todayStart.AddMinutes(1), "glm-5", 100, 10),
            Entry(todayStart.AddMinutes(2), "glm-5", 200, 20));
        codex = new FakeSource("codex", "Codex",
            Entry(todayStart.AddMinutes(3), "gpt-6", 1000, 100));

        var unavailable = new FakeSource("kimi", "Kimi Code") { Available = false };

        return new MultiAgentTokenStatsProvider(
            new EmptyProvider(),
            zcode, codex, unavailable);
    }

    [Fact]
    public async Task AllAgents_SumsEverySource()
    {
        var provider = CreateProvider(out _, out _);
        provider.SetAgentFilter(null);

        var stats = await provider.FetchAsync(0, "hour");

        Assert.Equal(100 + 10 + 200 + 20 + 1000 + 100, stats.Total.Total);
        Assert.Equal(3, stats.Steps);
    }

    [Fact]
    public async Task SingleAgent_OnlyCountsIt()
    {
        var provider = CreateProvider(out _, out _);
        provider.SetAgentFilter("codex");

        var stats = await provider.FetchAsync(0, "hour");

        Assert.Equal(1100, stats.Total.Total);
        Assert.Single(stats.Models);
        Assert.Equal("gpt-6", stats.Models[0].ModelId);
    }

    [Fact]
    public async Task SwitchingAgent_ChangesResultImmediately()
    {
        var provider = CreateProvider(out _, out _);

        provider.SetAgentFilter("zcode");
        var zcodeStats = await provider.FetchAsync(0, "hour");
        Assert.Equal(330, zcodeStats.Total.Total);

        provider.SetAgentFilter(null);
        var allStats = await provider.FetchAsync(0, "hour");
        Assert.Equal(1430, allStats.Total.Total);
    }

    [Fact]
    public void SourceDescription_ListsAvailableAgents()
    {
        var provider = CreateProvider(out _, out _);
        provider.SetAgentFilter(null);

        Assert.Contains("ZCode", provider.SourceDescription);
        Assert.Contains("Codex", provider.SourceDescription);
        Assert.DoesNotContain("Kimi", provider.SourceDescription);
    }

    [Fact]
    public async Task FilteredAgent_Unavailable_FallsBackToEmpty()
    {
        var provider = CreateProvider(out _, out _);
        provider.SetAgentFilter("kimi");

        var stats = await provider.FetchAsync(0, "hour");

        Assert.Equal(0, stats.Total.Total);
    }

    [Fact]
    public async Task OldEntries_OutsideWindow_AreExcluded()
    {
        // 今天 00:00:01 在窗口内；5 天前在窗口外
        var todayStart = DateTimeOffset.Now.Date.AddSeconds(1);
        var source = new FakeSource("zcode", "ZCode",
            Entry(todayStart, "glm-5", 100, 10),
            Entry(todayStart.AddDays(-5), "glm-5", 999_999, 0));

        var provider = new MultiAgentTokenStatsProvider(new EmptyProvider(), source);

        var stats = await provider.FetchAsync(3, "day");
        Assert.Equal(110, stats.Total.Total);
    }
}
