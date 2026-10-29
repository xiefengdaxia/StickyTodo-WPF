using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace StickyTodo.Updates;

/// <summary>
/// 基于 GitHub 仓库地址的「检查更新」。
///
/// 只做一件事：问 <c>repos/{owner}/{repo}/releases/latest</c>，
/// 把 tag 解析成版本号和本地版本比一比。所有网络异常都收敛成
/// <see cref="UpdateCheckStatus.Failed"/>，绝不抛给调用方，也绝不编造版本号。
/// </summary>
public sealed class GitHubUpdateChecker
{
    public const string UserAgentText = "StickyTodo-UpdateChecker";

    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient _http;
    private readonly Func<Version> _currentVersion;

    public GitHubUpdateChecker(
        UpdateRepository? repository = null,
        HttpClient? http = null,
        Func<Version>? currentVersion = null)
    {
        Repository = repository ?? UpdateRepository.StickyTodo;
        _http = http ?? SharedClient;
        _currentVersion = currentVersion ?? (() => AppVersionInfo.Current);
    }

    public UpdateRepository Repository { get; }

    /// <summary>只有正式发布才算，预发布版（pre-release）GitHub 不会返回。</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = _currentVersion();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Repository.LatestReleaseApiUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgentText);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.Failed(current, UpdateFailure.NoRelease);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return UpdateCheckResult.Failed(current, UpdateFailure.RateLimited, ((int)response.StatusCode).ToString());
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(current, UpdateFailure.Server, "HTTP " + (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Evaluate(current, json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 断网 / 代理拦截 / TLS 握手失败都走这里，原因原样给界面看
            return UpdateCheckResult.Failed(current, UpdateFailure.Network, ex.Message);
        }
    }

    /// <summary>把 GitHub 返回的 release JSON 变成检查结果（抽出来便于单测）。</summary>
    public static UpdateCheckResult Evaluate(Version current, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateCheckResult.Failed(current, UpdateFailure.BadResponse, "release JSON 不是对象");
            }

            var tag = ReadString(root, "tag_name");
            var pageUrl = ReadString(root, "html_url");
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(pageUrl))
            {
                return UpdateCheckResult.Failed(current, UpdateFailure.BadResponse, "缺少 tag_name / html_url");
            }

            if (!AppVersionInfo.TryParse(tag, out var latest))
            {
                return UpdateCheckResult.Failed(current, UpdateFailure.BadResponse, "tag 解析不出版本号：" + tag);
            }

            var release = new UpdateRelease(
                tag,
                latest,
                ReadString(root, "name") ?? string.Empty,
                ReadString(root, "body") ?? string.Empty,
                pageUrl,
                PickDownloadUrl(root, pageUrl),
                ReadDate(root, "published_at"));

            return latest > current
                ? UpdateCheckResult.Available(current, latest, release)
                : UpdateCheckResult.UpToDate(current, release, latest);
        }
        catch (JsonException ex)
        {
            return UpdateCheckResult.Failed(current, UpdateFailure.BadResponse, ex.Message);
        }
    }

    /// <summary>优先 x64 压缩包，其次任意压缩包，再次第一个附件，都没有就退回发布页。</summary>
    private static string PickDownloadUrl(JsonElement root, string fallback)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        string? best = null;
        string? firstAsset = null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = ReadString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            firstAsset ??= url;
            var name = ReadString(asset, "name") ?? string.Empty;

            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Contains("x64", StringComparison.OrdinalIgnoreCase)
                || name.Contains("amd64", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            best ??= url;
        }

        return best ?? firstAsset ?? fallback;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string property)
    {
        var text = ReadString(element, property);
        return DateTimeOffset.TryParse(text, out var value) ? value : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // 检查更新不该拖住界面；超时就报失败让用户重试
            Timeout = TimeSpan.FromSeconds(12),
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgentText);
        return client;
    }
}
