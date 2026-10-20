namespace StickyTodo.Core.Services;

/// <summary>
/// 待办排序的纯逻辑，与存储、界面解耦，便于单测。
/// </summary>
public static class TodoOrdering
{
    /// <summary>
    /// 把 <paramref name="movingId"/> 移到 <paramref name="targetId"/> 之前；
    /// <paramref name="targetId"/> 为 null 表示移到末尾。
    /// 用「参照物 id」而不是下标，这样“先按标签过滤再拖动”也不会错位。
    /// </summary>
    public static IReadOnlyList<string> MoveBefore(
        IReadOnlyList<string> orderedIds,
        string movingId,
        string? targetId)
    {
        if (movingId == targetId)
        {
            // 拖到自己身上 = 不改变顺序
            return orderedIds;
        }

        var list = orderedIds.Where(id => id != movingId).ToList();
        if (list.Count == orderedIds.Count)
        {
            // movingId 不在列表里（已被删除/不属于本看板）→ 原样返回，避免凭空插入
            return orderedIds;
        }

        var index = targetId is null ? list.Count : list.IndexOf(targetId);
        if (index < 0)
        {
            index = list.Count;
        }

        list.Insert(index, movingId);
        return list;
    }
}