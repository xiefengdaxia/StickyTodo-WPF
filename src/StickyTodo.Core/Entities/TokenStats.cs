namespace StickyTodo.Core.Entities;

/// <summary>五个方向的 token 用量。和 token 面板接口字段一一对应。</summary>
public sealed record TokenUsage(
    long Input,
    long Output,
    long Reasoning,
    long CacheRead,
    long CacheWrite)
{
    public static readonly TokenUsage Empty = new(0, 0, 0, 0, 0);

    public long Total => Input + Output + Reasoning + CacheRead + CacheWrite;

    /// <summary>缓存比例：缓存读 /（缓存读 + 输入）。</summary>
    public double CacheRatio
    {
        get
        {
            var denominator = CacheRead + Input;
            return denominator <= 0 ? 0 : (double)CacheRead / denominator;
        }
    }

    public static TokenUsage Add(TokenUsage a, TokenUsage b) => new(
        a.Input + b.Input,
        a.Output + b.Output,
        a.Reasoning + b.Reasoning,
        a.CacheRead + b.CacheRead,
        a.CacheWrite + b.CacheWrite);
}

/// <summary>趋势图上的一个点（按小时或按天）。</summary>
public sealed record TokenSeriesPoint(string Key, string Label, long Steps, TokenUsage Usage)
{
    public long Total => Usage.Total;
}

/// <summary>按模型聚合的一行。</summary>
public sealed record TokenModelUsage(string ModelId, string ProviderId, long Steps, TokenUsage Usage)
{
    public long Total => Usage.Total;

    public string DisplayName => ProviderId.Length == 0 ? ModelId : $"{ModelId} · {ProviderId}";
}

/// <summary>一次查询的完整结果。</summary>
public sealed record TokenStats(
    DateTimeOffset From,
    DateTimeOffset To,
    string Granularity,
    TokenUsage Total,
    long Steps,
    int ActiveDays,
    IReadOnlyList<TokenSeriesPoint> Series,
    IReadOnlyList<TokenModelUsage> Models)
{
    public static readonly TokenStats Empty = new(
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        "hour",
        TokenUsage.Empty,
        0,
        0,
        [],
        []);
}