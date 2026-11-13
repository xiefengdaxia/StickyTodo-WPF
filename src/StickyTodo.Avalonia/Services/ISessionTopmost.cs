namespace StickyTodo.Notes.Services;

/// <summary>
/// 看板窗口的「置顶」开关。开机自启时用它把窗口降回桌面层，
/// 只改这一次运行的状态，不写回设置——下次手动启动还是用户原来选的那样。
/// </summary>
public interface ISessionTopmost
{
    bool IsTopmost { get; set; }
}
