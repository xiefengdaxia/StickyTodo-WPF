namespace StickyTodo.Weather;

/// <summary>
/// 城市天气的内存缓存（整个进程一份）。
///
/// 为什么需要共享：桌面时钟和天气看板是两个窗口、两个 ViewModel，
/// 各自缓存的话"从时钟点进看板"仍然会重新请求。这里做成单例注入：
/// 谁取到了就写进来，另一边打开时先查这里。
///
/// 只缓存成功的结果；刷新按钮 / 重试走 force，直接请求并覆盖。
/// </summary>
public sealed class WeatherCache
{
    private readonly Dictionary<string, (WeatherReport Report, DateTime FetchedAt)> _map =
        new(StringComparer.Ordinal);

    private readonly object _gate = new();

    /// <summary>取缓存；没有则返回 false。</summary>
    public bool TryGet(string city, out WeatherReport? report, out DateTime fetchedAt)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(city, out var hit))
            {
                report = hit.Report;
                fetchedAt = hit.FetchedAt;
                return true;
            }
        }

        report = null;
        fetchedAt = default;
        return false;
    }

    /// <summary>写入一次成功的取数结果。</summary>
    public void Set(string city, WeatherReport report, DateTime fetchedAt)
    {
        lock (_gate)
        {
            _map[city] = (report, fetchedAt);
        }
    }

    /// <summary>清空（切换语言/时区之类需要重算的场合用；目前没人调）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
        }
    }
}
