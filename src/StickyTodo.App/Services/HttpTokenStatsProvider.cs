using System.Net.Http;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.App.Services;

/// <summary>
/// 从 OpenCode 的 token 面板取数据（GET {endpoint}/stats?days=N&amp;gran=hour|day）。
/// 这是本程序唯一的联网功能，单独隔离在这个类里，其它模块仍然完全本地。
/// 解析逻辑在 Core 的 <see cref="TokenStatsParser"/>，那边有单测。
/// </summary>
public sealed class HttpTokenStatsProvider : ITokenStatsProvider, IDisposable
{
    public const string DefaultEndpoint = "http://100.113.121.18:8199";

    private readonly HttpClient _http;

    public HttpTokenStatsProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>当前查询的服务地址，例如 http://100.113.121.18:8199。</summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    public async Task<TokenStats> FetchAsync(int days, string granularity, CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint.TrimEnd('/')}/stats?days={days}&gran={granularity}";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return TokenStatsParser.Parse(json);
    }

    public void Dispose() => _http.Dispose();
}