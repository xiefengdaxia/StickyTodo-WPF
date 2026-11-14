using System.Text.Json;

namespace StickyTodo.Mcp;

/// <summary>
/// 自定义看板的默认 HTML 模板：AI 没给 HTML 时用它兜底。
/// 契约与自定义 HTML 一致——宿主通过 WebView2 postMessage 推送：
///   { "type": "data",   "data": ...,       "fetchedAt": "..." }  → 采集到的数据
///   { "type": "error",  "message": "..." }                       → 采集失败
///   { "type": "board",  "name": "...",      "updatedAt": "..." } → 看板元信息
/// </summary>
public static class CustomBoardTemplate
{
    /// <summary>单份 HTML 上限（WebView2 NavigateToString 限 2MB，留余量）。</summary>
    public const int MaxHtmlBytes = 1 * 1024 * 1024;

    /// <summary>单份采集脚本上限。</summary>
    public const int MaxScriptBytes = 256 * 1024;

    public static string BuildHtml(string boardName) => $$"""

        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
            :root {
                color-scheme: light dark;
                --bg: transparent;
                --fg: #2f4234;
                --dim: #5e7364;
                --accent: #3e7b4f;
                --mono: ui-monospace, Consolas, 'Cascadia Mono', monospace;
            }
            @media (prefers-color-scheme: dark) {
                :root { --fg: #d6e2d8; --dim: #8aa192; --accent: #7dbd93; }
            }
            html, body { margin: 0; padding: 0; background: var(--bg); }
            body {
                font: 13px/1.5 -apple-system, 'Segoe UI', 'Microsoft YaHei', sans-serif;
                color: var(--fg); padding: 12px 14px;
                -webkit-user-select: text; user-select: text;
            }
            h1 { font-size: 14px; margin: 0 0 4px; color: var(--accent); }
            .meta { font-size: 11px; color: var(--dim); margin-bottom: 10px; }
            .error { color: #c0392b; font-size: 12px; margin-bottom: 8px; white-space: pre-wrap; }
            pre {
                margin: 0; font-family: var(--mono); font-size: 12px; white-space: pre-wrap;
                word-break: break-all;
            }
            .waiting { color: var(--dim); font-style: italic; }
        </style>
        </head>
        <body>
        <h1 id="title"></h1>
        <div class="meta" id="meta">等待数据…</div>
        <div class="error" id="error"></div>
        <pre id="content" class="waiting">等待采集器或 AI 推送数据…</pre>
        <script>
        var title = document.getElementById('title');
        var meta = document.getElementById('meta');
        var errorBox = document.getElementById('error');
        var content = document.getElementById('content');

        function render(data, fetchedAt) {
            errorBox.textContent = '';
            if (fetchedAt) { meta.textContent = '更新于 ' + fetchedAt; }
            if (typeof data === 'string') {
                content.textContent = data;
            } else {
                content.textContent = JSON.stringify(data, null, 2);
            }
            content.className = '';
        }

        function renderError(message) {
            errorBox.textContent = message || '';
        }

        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.addEventListener('message', function (event) {
                var msg = event.data || {};
                if (msg.type === 'data') { render(msg.data, msg.fetchedAt); }
                else if (msg.type === 'error') { renderError(msg.message); }
                else if (msg.type === 'board') { title.textContent = msg.name || ''; if (msg.updatedAt) { meta.textContent = msg.updatedAt; } }
            });
        }

        title.textContent = {{JsonSerializer.Serialize(boardName)}};
        </script>
        </body>
        </html>
        """;
}
