namespace StickyTodo.Core.Entities;

/// <summary>
/// 看板：一个看板对应一个独立窗口。
/// </summary>
public sealed class Board
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public int Sort { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>看板类型：待办清单 or 便签/随笔。</summary>
    public BoardKind Kind { get; set; } = BoardKind.Todo;

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>上次关闭时的窗口位置与大小，没记录过则为 null。</summary>
    public BoardWindowState? Window { get; set; }

    // ---------- 自定义看板（AI 通过 MCP 创建，仅 Kind = Custom 时有意义） ----------

    /// <summary>看板 HTML（WebView2 渲染），为空时用内置默认模板。</summary>
    public string? CustomHtml { get; set; }

    /// <summary>采集脚本（JS，须定义 collect()），为空表示没有采集器。</summary>
    public string? CustomScript { get; set; }

    /// <summary>采集间隔（秒），为空时用默认值。</summary>
    public int? CustomIntervalSeconds { get; set; }
}