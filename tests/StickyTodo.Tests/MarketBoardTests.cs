using StickyTodo.Mcp.Market;

namespace StickyTodo.Tests;

/// <summary>
/// 看板市场读取回归：一个文件夹 = 一个看板包，单个坏包只跳过它自己、不影响其它包。
/// 每个测试用独立临时目录（照 <see cref="DatabaseTestBase"/> 的写法），Dispose 时删掉。
/// </summary>
public sealed class MarketBoardTests : IDisposable
{
    private readonly string _root;

    public MarketBoardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"stickytodo-market-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试收尾，删不掉不影响结论
        }
    }

    /// <summary>造一个看板包目录：board.json + （默认）board.html。</summary>
    private string CreatePackage(string folderName, string? manifest, string? html = "<!DOCTYPE html><html><body>看板</body></html>")
    {
        var folder = Path.Combine(_root, folderName);
        Directory.CreateDirectory(folder);
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(folder, BoardMarket.ManifestName), manifest);
        }

        if (html is not null)
        {
            File.WriteAllText(Path.Combine(folder, "board.html"), html);
        }

        return folder;
    }

    private static void WriteFile(string folder, string fileName, string content) =>
        File.WriteAllText(Path.Combine(folder, fileName), content);

    [Fact]
    public void Load_ReadsCompletePackage()
    {
        var folder = CreatePackage("price-calc", """
            {
              "id": "price-calc",
              "name": "价格计算",
              "summary": "算一算更安心",
              "description": "详细说明不该混进 summary",
              "author": "测试作者",
              "version": "1.2.3",
              "intervalSeconds": 300,
              "tags": ["工具", "计算"],
              "html": "board.html",
              "script": "board.js",
              "preview": "preview.png"
            }
            """);
        WriteFile(folder, "board.js", "function collect() { return { price: 1 }; }");
        WriteFile(folder, "preview.png", "fake-png-bytes");

        var board = Assert.Single(BoardMarket.Load(_root));

        Assert.Equal("price-calc", board.Id);
        Assert.Equal("价格计算", board.Name);
        Assert.Equal("算一算更安心", board.Summary);
        Assert.Equal("测试作者", board.Author);
        Assert.Equal("1.2.3", board.Version);
        Assert.Equal(300, board.IntervalSeconds);
        Assert.Equal(new[] { "工具", "计算" }, board.Tags);
        Assert.Contains("看板", board.Html);
        Assert.NotNull(board.Script);
        Assert.Contains("collect", board.Script);
        Assert.Equal(Path.Combine(folder, "preview.png"), board.PreviewPath);
        Assert.Equal(folder, board.Directory);
    }

    [Fact]
    public void Load_HtmlFieldMissing_UsesBoardHtml()
    {
        CreatePackage("no-html-key", """
            { "id": "no-html-key", "name": "没写 html 字段" }
            """);

        var board = Assert.Single(BoardMarket.Load(_root));

        Assert.Contains("看板", board.Html);
        Assert.Null(board.Script);
        Assert.Null(board.PreviewPath);
        Assert.Null(board.IntervalSeconds);
        Assert.Empty(board.Tags);
    }

    [Fact]
    public void Load_IdAndNameMissing_FallsBackToFolderName()
    {
        var folder = CreatePackage("fallback-name", """{ "summary": "只有摘要" }""");

        var board = Assert.Single(BoardMarket.Load(_root));

        Assert.Equal("fallback-name", board.Id);
        Assert.Equal("fallback-name", board.Name);
        Assert.Equal("只有摘要", board.Summary);
        Assert.Equal(folder, board.Directory);
    }

    [Fact]
    public void Load_BrokenJsonManifest_SkipsOnlyThatPackage()
    {
        CreatePackage("good", """{ "id": "good", "name": "好的" }""");
        CreatePackage("broken", "{ 这不是一个合法的 JSON 清单 ");

        var boards = BoardMarket.Load(_root);

        Assert.Equal(new[] { "good" }, boards.Select(b => b.Id));
    }

    [Fact]
    public void Load_MissingHtmlFile_SkipsPackage()
    {
        CreatePackage("lost-html", """{ "id": "lost-html", "name": "丢了页面", "html": "not-here.html" }""", html: null);

        Assert.Empty(BoardMarket.Load(_root));
    }

    [Fact]
    public void Load_HtmlOverLimit_SkipsPackage()
    {
        var atLimit = CreatePackage("at-limit", """{ "id": "at-limit", "name": "刚好到上限" }""");
        WriteFile(atLimit, "board.html", new string('x', BoardMarket.MaxHtmlBytes));

        var tooBig = CreatePackage("too-big", """{ "id": "too-big", "name": "超上限" }""");
        WriteFile(tooBig, "board.html", new string('x', 1_000_001));

        var boards = BoardMarket.Load(_root);

        Assert.Equal(1_000_000, BoardMarket.MaxHtmlBytes);
        Assert.Equal(new[] { "at-limit" }, boards.Select(b => b.Id));
    }

    [Fact]
    public void Load_ScriptFileMissing_ScriptIsNull()
    {
        var scripted = CreatePackage("with-script", """
            { "id": "with-script", "name": "有脚本", "script": "board.js" }
            """);
        WriteFile(scripted, "board.js", "function collect() { return 1; }");

        CreatePackage("script-gone", """
            { "id": "script-gone", "name": "脚本丢了", "script": "board.js" }
            """);

        var boards = BoardMarket.Load(_root);

        // 两个包都在（顺序无关），一个有脚本、一个脚本文件丢了
        Assert.Equal(new[] { "script-gone", "with-script" }, boards.Select(b => b.Id).OrderBy(id => id, StringComparer.Ordinal));
        var withScript = Assert.Single(boards, b => b.Id == "with-script");
        Assert.NotNull(withScript.Script);
        Assert.Contains("collect", withScript.Script);
        Assert.Null(Assert.Single(boards, b => b.Id == "script-gone").Script);
    }

    [Fact]
    public void Load_MissingOrEmptyDirectory_ReturnsEmpty()
    {
        Assert.Empty(BoardMarket.Load(Path.Combine(_root, "not-exists")));
        Assert.Empty(BoardMarket.Load(string.Empty));
    }
}
