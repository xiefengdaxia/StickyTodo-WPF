namespace StickyTodo.Core.Services;

/// <summary>待办列表的两种视图。</summary>
public enum TodoViewMode
{
    /// <summary>只看未完成的（默认）。</summary>
    Todo = 0,

    /// <summary>只看已完成的。</summary>
    Done = 1,
}

/// <summary>
/// 视图筛选的纯逻辑。放在 Core 里是为了能直接单测，
/// 界面只负责调用和渲染。
/// </summary>
public static class TodoViewFilter
{
    public static bool Matches(bool isDone, TodoViewMode mode) =>
        mode == TodoViewMode.Done ? isDone : !isDone;

    public static IReadOnlyList<T> Filter<T>(
        IEnumerable<T> items,
        TodoViewMode mode,
        Func<T, bool> isDone) =>
        items.Where(item => Matches(isDone(item), mode)).ToList();

    public static string DisplayName(TodoViewMode mode) =>
        mode == TodoViewMode.Done ? "已完成" : "待办";

    public static string EmptyHint(TodoViewMode mode) =>
        mode == TodoViewMode.Done ? "还没有已完成的事项" : "还没有待办，写下第一条吧";

    /// <summary>
    /// 视图内的排序：待办视图沿用原有手动顺序（sort），
    /// 已完成视图按完成时间倒序（最近完成的在最上面）。
    /// </summary>
    public static IReadOnlyList<T> SortForView<T>(
        IEnumerable<T> items,
        TodoViewMode mode,
        Func<T, int> sortKey,
        Func<T, DateTimeOffset?> doneAt)
    {
        if (mode == TodoViewMode.Done)
        {
            return items
                .OrderByDescending(item => doneAt(item) ?? DateTimeOffset.MinValue)
                .ThenBy(sortKey)
                .ToList();
        }

        return items.OrderBy(sortKey).ToList();
    }
}