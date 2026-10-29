namespace StickyTodo.Updates;

/// <summary>检查更新的结果类型。</summary>
public enum UpdateCheckStatus
{
    /// <summary>已经是最新版本。</summary>
    UpToDate,

    /// <summary>仓库里有比当前更新的版本。</summary>
    Available,

    /// <summary>没查成（没网 / 限流 / 解析失败 / 仓库还没发版）。</summary>
    Failed,
}

/// <summary>失败原因（界面按它挑文案，细节放 <see cref="UpdateCheckResult.Detail"/>）。</summary>
public enum UpdateFailure
{
    None,

    /// <summary>仓库里还没有正式发布。</summary>
    NoRelease,

    /// <summary>请求没发出去或连接被拦（TLS / 代理 / 断网）。</summary>
    Network,

    /// <summary>GitHub 限流（未登录 60 次/小时）。</summary>
    RateLimited,

    /// <summary>服务器返回了别的错误码。</summary>
    Server,

    /// <summary>返回的 JSON 不认识（字段缺失 / tag 解析不出）。</summary>
    BadResponse,
}

/// <summary>一次检查更新的结果。</summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version CurrentVersion,
    Version? LatestVersion,
    UpdateRelease? Release,
    UpdateFailure Failure,
    string? Detail)
{
    public bool HasUpdate => Status == UpdateCheckStatus.Available;

    public static UpdateCheckResult UpToDate(Version current, UpdateRelease? release, Version? latest) =>
        new(UpdateCheckStatus.UpToDate, current, latest, release, UpdateFailure.None, null);

    public static UpdateCheckResult Available(Version current, Version latest, UpdateRelease release) =>
        new(UpdateCheckStatus.Available, current, latest, release, UpdateFailure.None, null);

    public static UpdateCheckResult Failed(Version current, UpdateFailure failure, string? detail = null) =>
        new(UpdateCheckStatus.Failed, current, null, null, failure, detail);
}
