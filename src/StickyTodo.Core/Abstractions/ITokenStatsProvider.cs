using StickyTodo.Core.Entities;

namespace StickyTodo.Core.Abstractions;

/// <summary>
/// Token 用量数据源。放在 Core 里是为了让 ViewModel 可测，
/// 具体实现（HTTP）在 App 层。
/// </summary>
public interface ITokenStatsProvider
{
    /// <summary>
    /// 取用量统计。<paramref name="days"/> 为 0 表示今天，3/7/30 表示近 N 天；
    /// <paramref name="granularity"/> 取 hour 或 day。
    /// </summary>
    Task<TokenStats> FetchAsync(int days, string granularity, CancellationToken cancellationToken = default);
}