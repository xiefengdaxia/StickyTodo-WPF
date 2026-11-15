namespace StickyTodo.Infrastructure;

/// <summary>
/// 自定义看板的「网页界面状态」持久化（区间选择、模型勾选、折叠状态等）。
///
/// 为什么需要它：自定义看板用 <c>NavigateToString</c> 加载，页面是 opaque origin，
/// <c>localStorage</c> 会被浏览器直接拒绝（SecurityError），所以网页自己存不住东西。
/// 页面通过 <c>postMessage</c> 把状态交给宿主，宿主落到
/// <c>%LOCALAPPDATA%\StickyTodo\board-ui\&lt;boardId&gt;.json</c>，下次打开再推回页面。
///
/// 安全边界：只接受 64KB 以内的 JSON 文本，任何异常都吞掉（界面状态丢了不影响看板本身）。
/// </summary>
public static class BoardUiStateStore
{
    /// <summary>单份状态的字节上限。</summary>
    public const int MaxBytes = 64 * 1024;

    private static string Directory => Path.Combine(AppPaths.DataDirectory, "board-ui");

    /// <summary>读回某看板的界面状态；没有或读失败返回 null。</summary>
    public static string? Load(string boardId)
    {
        try
        {
            var path = PathFor(boardId);
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) || text.Length > MaxBytes ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>保存某看板的界面状态（覆盖写）。</summary>
    public static void Save(string boardId, string json)
    {
        if (string.IsNullOrWhiteSpace(boardId) || string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(PathFor(boardId), json);
        }
        catch (Exception)
        {
            // 界面状态是锦上添花，写失败不影响看板
        }
    }

    private static string PathFor(string boardId)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            boardId = boardId.Replace(invalid, '_');
        }

        return Path.Combine(Directory, boardId + ".json");
    }
}
