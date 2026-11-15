using StickyTodo.Infrastructure;

namespace StickyTodo.Tests;

/// <summary>
/// 看板网页界面状态持久化回归。
/// 注意：<see cref="BoardUiStateStore"/> 固定落在 <see cref="AppPaths.DataDirectory"/> 下的
/// <c>board-ui</c>（AppPaths 的目录是进程级 Lazy，测试里改不了），
/// 所以这里只用唯一的 boardId，并在 Dispose 里删掉自己生成的文件，不碰真实数据。
/// </summary>
public sealed class BoardUiStateStoreTests : IDisposable
{
    private readonly List<string> _boardIds = new();

    /// <summary>本次测试专用的 boardId（前缀 test- 保证不会和真实看板 id 撞上）。</summary>
    private string NewBoardId(string suffix = "")
    {
        var id = "test-" + Guid.NewGuid().ToString("N") + suffix;
        _boardIds.Add(id);
        return id;
    }

    public void Dispose()
    {
        foreach (var id in _boardIds)
        {
            try
            {
                File.Delete(PathFor(id));
            }
            catch (IOException)
            {
                // 测试收尾，删不掉不影响结论
            }
        }
    }

    /// <summary>落盘路径：和实现一致，非法文件名字符替换成下划线。</summary>
    private static string PathFor(string boardId)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            boardId = boardId.Replace(invalid, '_');
        }

        return Path.Combine(AppPaths.DataDirectory, "board-ui", boardId + ".json");
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var id = NewBoardId();
        const string json = """{"range":"week","models":["a","b"],"note":"中文也要一致","collapsed":true}""";

        BoardUiStateStore.Save(id, json);

        Assert.Equal(json, BoardUiStateStore.Load(id));
    }

    [Fact]
    public void SaveThenLoad_OverwritesPreviousState()
    {
        var id = NewBoardId();

        BoardUiStateStore.Save(id, """{"tab":"tl"}""");
        BoardUiStateStore.Save(id, """{"tab":"br"}""");

        Assert.Equal("""{"tab":"br"}""", BoardUiStateStore.Load(id));
    }

    [Fact]
    public void Load_UnsavedId_ReturnsNull()
    {
        Assert.Null(BoardUiStateStore.Load(NewBoardId()));
    }

    [Fact]
    public void Save_JsonOverMaxBytes_IsIgnored()
    {
        var id = NewBoardId();
        var tooLarge = "{\"pad\":\"" + new string('x', BoardUiStateStore.MaxBytes) + "\"}";

        BoardUiStateStore.Save(id, tooLarge);

        Assert.False(File.Exists(PathFor(id)), "超过上限的状态不应该落盘");
        Assert.Null(BoardUiStateStore.Load(id));
    }

    [Fact]
    public void SaveAndLoad_IdWithInvalidFileNameChars_DoesNotThrow()
    {
        var id = NewBoardId("/a:b");
        const string json = """{"tab":"tl"}""";

        BoardUiStateStore.Save(id, json);

        Assert.True(File.Exists(PathFor(id)), "非法字符应该被替换成下划线后落盘");
        Assert.Equal(json, BoardUiStateStore.Load(id));
    }
}
