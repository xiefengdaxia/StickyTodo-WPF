using StickyTodo.Core.Entities;

namespace StickyTodo.Core.Abstractions;

/// <summary>
/// 一个 Agent 客户端的本地用量账本（zcode 的 SQLite、codex 的 rollout JSONL…）。
/// <para>
/// 实现约定：
/// 1. 只读，绝不写客户端的文件；
/// 2. <see cref="ReadEntries"/> 返回区间内的"一次模型调用"粒度记录，
///    由调用方聚合——这样多源合并与单源统计走同一套代码；
/// 3. 客户端没安装 / 没有任何数据时 <see cref="IsAvailable"/> 为 false，
///    上层不显示它（不显示一个永远为零的项）。
/// </para>
/// </summary>
public interface IAgentTokenSource
{
    /// <summary>稳定 id（设置持久化、下拉框 value 用），如 "zcode"。</summary>
    string AgentId { get; }

    /// <summary>显示名，如 "ZCode"。</summary>
    string DisplayName { get; }

    /// <summary>数据源说明（拼进看板副标题，让用户确认看的是哪个目录）。</summary>
    string SourceDescription { get; }

    /// <summary>客户端已安装且存在数据。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 读取 [fromDay, 今天] 的调用明细。实现方自己缓存，重复调用应廉价。
    /// </summary>
    IEnumerable<AgentUsageEntry> ReadEntries(DateOnly fromDay);
}

/// <summary>
/// 一次「模型调用」的最小聚合记录。
/// <para>
/// <c>Steps</c> = 这条记录代表多少次调用：大多数源一条记录就是一次调用（默认 1），
/// 但 opencode 是在 SQL 里按「天 × 小时 × 模型」分组的，一条记录可能是几十次调用，
/// 必须把真实次数带上来；否则看板的「调用次数」会按分组个数算，严重偏小。
/// </para>
/// </summary>
public sealed record AgentUsageEntry(
    DateTimeOffset Timestamp,
    string ModelId,
    string ProviderId,
    TokenUsage Usage,
    long Steps = 1);
