namespace StickyTodo.Core.Entities;

/// <summary>看板类型：待办清单，或一张白纸式的便签/随笔。</summary>
public enum BoardKind
{
    Todo = 0,
    Memo = 1,
}

public static class BoardKinds
{
    public const string Todo = "todo";
    public const string Memo = "memo";

    public static string ToStorage(BoardKind kind) => kind == BoardKind.Memo ? Memo : Todo;

    public static BoardKind Parse(string? value) =>
        string.Equals(value, Memo, StringComparison.OrdinalIgnoreCase) ? BoardKind.Memo : BoardKind.Todo;

    public static string DisplayName(BoardKind kind) => kind == BoardKind.Memo ? "便签看板" : "待办看板";
}