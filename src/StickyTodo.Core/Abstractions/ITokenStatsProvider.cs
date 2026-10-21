using StickyTodo.Core.Entities;

namespace StickyTodo.Core.Abstractions;

/// <summary>
/// Token 用量数据源。数据全部来自本机 opencode 的数据库，不联网。
/// 放在 Core 里是为了让 ViewModel 可测，具体读库实现在 Infrastructure 层。
/// </summary>
public interface ITokenStatsProvider
{
    /// <summary>数据来源的可读说明（显示在界面上，方便确认读的是哪个库）。</summary>
    string SourceDescription { get; }

    /// <summary>数据源当前是否可用（库不存在/读不了时为 false，界面据此提示）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 取用量统计。<paramref name="days"/> 为 0 表示今天，3/7/30 表示近 N 天；
    /// <paramref name="granularity"/> 取 hour 或 day。
    /// </summary>
    Task<TokenStats> FetchAsync(int days, string granularity, CancellationToken cancellationToken = default);
}