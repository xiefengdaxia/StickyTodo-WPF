namespace StickyTodo.Core.Entities;

/// <summary>看板类型：待办清单、便签/随笔、日历、Token 用量。</summary>
public enum BoardKind
{
    Todo = 0,
    Memo = 1,
    Calendar = 2,
    Token = 3,
}

public static class BoardKinds
{
    public const string Todo = "todo";
    public const string Memo = "memo";
    public const string Calendar = "calendar";
    public const string Token = "token";

    public static string ToStorage(BoardKind kind) => kind switch
    {
        BoardKind.Memo => Memo,
        BoardKind.Calendar => Calendar,
        BoardKind.Token => Token,
        _ => Todo,
    };

    public static BoardKind Parse(string? value) => value?.ToLowerInvariant() switch
    {
        Memo => BoardKind.Memo,
        Calendar => BoardKind.Calendar,
        Token => BoardKind.Token,
        _ => BoardKind.Todo,
    };

    public static string DisplayName(BoardKind kind) => kind switch
    {
        BoardKind.Memo => "便签看板",
        BoardKind.Calendar => "日历看板",
        BoardKind.Token => "Token 看板",
        _ => "待办看板",
    };
}