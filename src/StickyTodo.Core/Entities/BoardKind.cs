namespace StickyTodo.Core.Entities;

/// <summary>看板类型：待办清单、便签/随笔、日历、Token 用量、天气、世界时钟、桌面时钟。</summary>
public enum BoardKind
{
    Todo = 0,
    Memo = 1,
    Calendar = 2,
    Token = 3,
    Weather = 4,
    WorldClock = 5,
    DesktopClock = 6,
}

public static class BoardKinds
{
    public const string Todo = "todo";
    public const string Memo = "memo";
    public const string Calendar = "calendar";
    public const string Token = "token";
    public const string Weather = "weather";
    public const string WorldClock = "worldclock";
    public const string DesktopClock = "desktopclock";

    public static string ToStorage(BoardKind kind) => kind switch
    {
        BoardKind.Memo => Memo,
        BoardKind.Calendar => Calendar,
        BoardKind.Token => Token,
        BoardKind.Weather => Weather,
        BoardKind.WorldClock => WorldClock,
        BoardKind.DesktopClock => DesktopClock,
        _ => Todo,
    };

    public static BoardKind Parse(string? value) => value?.ToLowerInvariant() switch
    {
        Memo => BoardKind.Memo,
        Calendar => BoardKind.Calendar,
        Token => BoardKind.Token,
        Weather => BoardKind.Weather,
        WorldClock => BoardKind.WorldClock,
        DesktopClock => BoardKind.DesktopClock,
        _ => BoardKind.Todo,
    };

    public static string DisplayName(BoardKind kind) => kind switch
    {
        BoardKind.Memo => "便签看板",
        BoardKind.Calendar => "日历看板",
        BoardKind.Token => "Token 看板",
        BoardKind.Weather => "天气看板",
        BoardKind.WorldClock => "世界时钟",
        BoardKind.DesktopClock => "桌面时钟",
        _ => "待办看板",
    };

    /// <summary>是否是「工具型」看板：没有待办数据，只有一块面板。</summary>
    public static bool IsToolBoard(BoardKind kind) => kind is BoardKind.Weather or BoardKind.WorldClock or BoardKind.DesktopClock;
}
