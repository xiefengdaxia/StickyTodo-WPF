using System.Net;
using System.Net.Http;
using StickyTodo.Updates;

namespace StickyTodo.Tests;

public sealed class UpdateCheckerTests
{
    // ---------------- 版本解析 ----------------

    [Theory]
    [InlineData("1.5.6", "1.5.6")]
    [InlineData("v1.5.6", "1.5.6")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("1.5.6+5770df0", "1.5.6")]
    [InlineData("1.6.0-beta.1", "1.6.0")]
    [InlineData("release-1.5", "1.5.0")]
    [InlineData("3", "3.0.0")]
    public void TryParse_接受常见写法(string text, string expected)
    {
        Assert.True(AppVersionInfo.TryParse(text, out var version));
        Assert.Equal(expected, AppVersionInfo.Display(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData(null)]
    public void TryParse_拒绝非版本文本(string? text)
    {
        Assert.False(AppVersionInfo.TryParse(text, out _));
    }

    // ---------------- 仓库地址 ----------------

    [Theory]
    [InlineData("https://github.com/xiefengdaxia/StickyTodo-WPF", "xiefengdaxia/StickyTodo-WPF")]
    [InlineData("https://github.com/xiefengdaxia/StickyTodo-WPF.git", "xiefengdaxia/StickyTodo-WPF")]
    [InlineData("git@github.com:xiefengdaxia/StickyTodo-WPF.git", "xiefengdaxia/StickyTodo-WPF")]
    [InlineData("xiefengdaxia/StickyTodo-WPF", "xiefengdaxia/StickyTodo-WPF")]
    public void FromUrl_解析出仓库(string url, string expected)
    {
        Assert.Equal(expected, UpdateRepository.FromUrl(url).Slug);
    }

    [Fact]
    public void FromUrl_地址里没有仓库时抛异常()
    {
        Assert.Throws<ArgumentException>(() => UpdateRepository.FromUrl("https://github.com"));
    }

    [Fact]
    public void 默认仓库就是本项目的_origin()
    {
        Assert.Equal("xiefengdaxia/StickyTodo-WPF", UpdateRepository.StickyTodo.Slug);
        Assert.Equal(
            "https://api.github.com/repos/xiefengdaxia/StickyTodo-WPF/releases/latest",
            UpdateRepository.StickyTodo.LatestReleaseApiUrl);
    }

    // ---------------- JSON 解析 ----------------

    [Fact]
    public void Evaluate_新版本时给出下载链接与说明()
    {
        var json = """
        {
          "tag_name": "v1.9.0",
          "name": "StickyTodo 1.9.0",
          "html_url": "https://github.com/xiefengdaxia/StickyTodo-WPF/releases/tag/v1.9.0",
          "body": "### 新增\n- 检查更新",
          "published_at": "2026-10-29T12:00:00Z",
          "assets": [
            { "name": "StickyTodo-1.9.0-win-arm64.zip", "browser_download_url": "https://example.com/arm64.zip" },
            { "name": "StickyTodo-1.9.0-win-x64.zip", "browser_download_url": "https://example.com/x64.zip" }
          ]
        }
        """;

        var result = GitHubUpdateChecker.Evaluate(new Version(1, 5, 6), json);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.True(result.HasUpdate);
        Assert.Equal("1.9.0", AppVersionInfo.Display(result.LatestVersion));
        Assert.Equal("https://example.com/x64.zip", result.Release!.DownloadUrl);
        Assert.Equal("v1.9.0", result.Release.TagName);
        Assert.Contains("检查更新", result.Release.Notes);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T12:00:00Z"), result.Release.PublishedAt);
    }

    [Fact]
    public void Evaluate_同版本或更旧时是已是最新()
    {
        var same = GitHubUpdateChecker.Evaluate(new Version(1, 5, 6), Release("v1.5.6"));
        var older = GitHubUpdateChecker.Evaluate(new Version(1, 6, 0), Release("v1.5.6"));

        Assert.Equal(UpdateCheckStatus.UpToDate, same.Status);
        Assert.False(same.HasUpdate);
        Assert.Equal(UpdateCheckStatus.UpToDate, older.Status);
    }

    [Fact]
    public void Evaluate_没有附件时退回发布页()
    {
        var result = GitHubUpdateChecker.Evaluate(new Version(1, 0, 0), Release("v1.1.0", withAssets: false));

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(
            "https://github.com/xiefengdaxia/StickyTodo-WPF/releases/tag/v1.1.0",
            result.Release!.DownloadUrl);
    }

    [Fact]
    public void Evaluate_缺字段或坏JSON都是BadResponse()
    {
        var missingTag = GitHubUpdateChecker.Evaluate(new Version(1, 0, 0), """{ "html_url": "https://example.com" }""");
        var badJson = GitHubUpdateChecker.Evaluate(new Version(1, 0, 0), "{ not json");

        Assert.Equal(UpdateFailure.BadResponse, missingTag.Failure);
        Assert.Equal(UpdateFailure.BadResponse, badJson.Failure);
        Assert.Equal(UpdateCheckStatus.Failed, badJson.Status);
    }

    [Fact]
    public void Evaluate_tag解析不出时不当作有新版本()
    {
        var result = GitHubUpdateChecker.Evaluate(new Version(1, 0, 0), Release("nightly"));

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateFailure.BadResponse, result.Failure);
        Assert.Null(result.Release);
    }

    // ---------------- HTTP ----------------

    [Fact]
    public async Task CheckAsync_有新版时返回可用更新()
    {
        var handler = new StubHandler(_ => Json(Release("v2.0.0")));
        var checker = new GitHubUpdateChecker(
            UpdateRepository.StickyTodo,
            new HttpClient(handler),
            () => new Version(1, 5, 6));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("2.0.0", AppVersionInfo.Display(result.LatestVersion));
        Assert.Equal("https://api.github.com/repos/xiefengdaxia/StickyTodo-WPF/releases/latest", handler.LastUri);
        Assert.Equal("StickyTodo-UpdateChecker", handler.LastUserAgent);
    }

    [Fact]
    public async Task CheckAsync_仓库没发版时提示NoRelease()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = new GitHubUpdateChecker(UpdateRepository.StickyTodo, new HttpClient(handler), () => new Version(1, 5, 6));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateFailure.NoRelease, result.Failure);
    }

    [Fact]
    public async Task CheckAsync_限流时提示RateLimited()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var checker = new GitHubUpdateChecker(UpdateRepository.StickyTodo, new HttpClient(handler), () => new Version(1, 5, 6));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateFailure.RateLimited, result.Failure);
    }

    [Fact]
    public async Task CheckAsync_其他错误码提示Server()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var checker = new GitHubUpdateChecker(UpdateRepository.StickyTodo, new HttpClient(handler), () => new Version(1, 5, 6));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateFailure.Server, result.Failure);
        Assert.Equal("HTTP 500", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_网络异常不抛出而是变成失败态()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("The SSL connection could not be established"));
        var checker = new GitHubUpdateChecker(UpdateRepository.StickyTodo, new HttpClient(handler), () => new Version(1, 5, 6));

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateFailure.Network, result.Failure);
        Assert.Contains("SSL", result.Detail);
    }

    // ---------------- 发布说明排版 ----------------

    [Fact]
    public void ReleaseNotes_去掉井号与列表符号()
    {
        var text = ReleaseNotes.ToPlainText("### 新增\n- 第一条\n\n\n### 修复\n* 第二条\n```\ncode\n```");

        Assert.DoesNotContain("#", text);
        Assert.Contains("• 第一条", text);
        Assert.Contains("• 第二条", text);
        // 代码块只去掉围栏，里面的命令保留（常常是下载/安装说明）
        Assert.DoesNotContain("```", text);
        Assert.Contains("code", text);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void ReleaseNotes_空说明返回空串()
    {
        Assert.Equal(string.Empty, ReleaseNotes.ToPlainText(null));
        Assert.Equal(string.Empty, ReleaseNotes.ToPlainText("   "));
    }

    // ---------------- 辅助 ----------------

    private static string Release(string tag, bool withAssets = true) => withAssets
        ? $$"""
          {
            "tag_name": "{{tag}}",
            "name": "StickyTodo {{tag}}",
            "html_url": "https://github.com/xiefengdaxia/StickyTodo-WPF/releases/tag/{{tag}}",
            "body": "说明",
            "published_at": "2026-10-29T12:00:00Z",
            "assets": [
              { "name": "StickyTodo-win-x64.zip", "browser_download_url": "https://example.com/x64.zip" }
            ]
          }
          """
        : $$"""
          {
            "tag_name": "{{tag}}",
            "name": "StickyTodo {{tag}}",
            "html_url": "https://github.com/xiefengdaxia/StickyTodo-WPF/releases/tag/{{tag}}",
            "body": "",
            "assets": []
          }
          """;

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public string? LastUri { get; private set; }

        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri?.ToString();
            LastUserAgent = request.Headers.TryGetValues("User-Agent", out var values) ? values.FirstOrDefault() : null;
            return Task.FromResult(_respond(request));
        }
    }
}
