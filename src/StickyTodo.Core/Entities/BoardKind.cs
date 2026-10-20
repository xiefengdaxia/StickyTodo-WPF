namespace StickyTodo.Core.Entities;

/// <summary>看板类型：待办清单，或一张白纸式的便签/随笔。</summary>
public enum BoardKind
{
    Todo = 0,
    Memo = 1,
    Calendar = 2,
}

public static class BoardKinds
{
    public const string Todo = "todo";
    public const string Memo = "memo";
    public const string Calendar = "calendar";

    public static string ToStorage(BoardKind kind) => kind switch
    {
        BoardKind.Memo => Memo,
        BoardKind.Calendar => Calendar,
        _ => Todo,
    };

    public static BoardKind Parse(string? value) => value?.ToLowerInvariant() switch
    {
        Memo => BoardKind.Memo,
        Calendar => BoardKind.Calendar,
        _ => BoardKind.Todo,
    };

    public static string DisplayName(BoardKind kind) => kind switch
    {
        BoardKind.Memo => "便签看板",
        BoardKind.Calendar => "日历看板",
        _ => "待办看板",
    };
}