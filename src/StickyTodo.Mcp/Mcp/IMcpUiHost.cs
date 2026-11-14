namespace StickyTodo.Mcp;

/// <summary>
/// MCP 工具里需要碰 UI 的那部分动作。协议层放在本程序集（与 UI 框架无关），
/// 两端（WPF / Avalonia）各自实现这个接口并注入。
/// </summary>
public interface IMcpUiHost
{
    /// <summary>把动作派发到 UI 线程并等待完成（看板窗口的创建/关闭必须回 UI 线程）。</summary>
    T RunOnUi<T>(Func<T> action);

    /// <summary>把动作派发到 UI 线程并等待完成。</summary>
    void RunOnUi(Action action);

    /// <summary>打开（或激活）指定看板的窗口。看板不存在返回 false。</summary>
    bool OpenBoard(string boardId);

    /// <summary>关闭指定看板的窗口。窗口本来就没开返回 true。</summary>
    bool CloseBoard(string boardId);

    /// <summary>当前打开的看板窗口 id 列表。</summary>
    IReadOnlyList<string> OpenBoardIds { get; }

    /// <summary>数据被 MCP 改过之后，刷新所有打开的数据看板（让屏幕上的列表立刻可见）。</summary>
    Task RefreshOpenBoardsAsync();

    /// <summary>自定义看板的 HTML/脚本被 MCP 改过：让窗口重新加载内容（没开就不用做）。</summary>
    void ReloadCustomBoard(string boardId);

    /// <summary>护眼相关设置被 MCP 改过：让滤镜/色温/gamma 立即生效。</summary>
    void ApplyEyeSettings();

    /// <summary>托盘气泡 / Toast 通知。</summary>
    void Notify(string title, string message);

    /// <summary>截取整个虚拟桌面，PNG 字节。当前栈不支持时返回 null。</summary>
    Task<byte[]?> CaptureScreenshotAsync(CancellationToken cancellation = default);
}
