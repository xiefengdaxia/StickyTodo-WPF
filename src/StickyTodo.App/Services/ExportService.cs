using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace StickyTodo.App.Services;

/// <summary>导出格式：便签用前三种，待办用 Markdown / CSV / JSON。</summary>
public enum ExportFormat
{
    Markdown,
    Html,
    Text,
    Csv,
    Json,
}

/// <summary>
/// 导出用的一条待办快照。和 UI 的行对象解耦，方便序列化 / 拼 CSV，
/// 也避免导出过程中 UI 那边又改了数据导致半新半旧。
/// </summary>
public sealed record TodoExportRow(
    string Id,
    string Title,
    bool IsDone,
    DateTimeOffset? DueAt,
    string? Note,
    IReadOnlyList<string> Tags);

/// <summary>
/// 便签 / 待办的导出：选路径、拼内容、落盘。UI 只负责拿数据和提示成功。
///
/// 便签：Markdown（原文）、HTML（同一套预览逻辑转出来的，带内联 CSS）、纯文本（原文）。
/// 待办：Markdown（`- [x] 标题`，带截止时间）、CSV（标题,状态,截止,备注）、JSON（数组）。
/// </summary>
public static class ExportService
{
    /// <summary>弹「另存为」，用户取消就返回 null。</summary>
    public static string? AskSavePath(Window? owner, string baseName, ExportFormat format)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SafeFileName(baseName),
            DefaultExt = ExtensionOf(format),
            Filter = FilterOf(format),
        };

        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true && !string.IsNullOrWhiteSpace(dialog.FileName) ? dialog.FileName : null;
    }

    /// <summary>写文件：默认 UTF-8 带 BOM（Windows 上记事本 / Excel 才不会把中文认成乱码）。</summary>
    public static void WriteText(string path, string? content, bool withBom = true) =>
        File.WriteAllText(path, content ?? string.Empty, new UTF8Encoding(withBom));

    // ==================== 便签 ====================

    /// <summary>导出 Markdown：正文本来就是 Markdown，原样存。</summary>
    public static string MemoMarkdown(string? content) => EnsureTrailingNewline(content);

    /// <summary>导出纯文本：也是原文（不做脱标记，避免用户以为丢了东西）。</summary>
    public static string MemoText(string? content) => EnsureTrailingNewline(content);

    /// <summary>导出 HTML：和预览走同一套 Markdown 解析，外面包一层内联 CSS 的壳。</summary>
    public static string MemoHtml(string? content, string? title)
    {
        var heading = string.IsNullOrWhiteSpace(title) ? "Memo" : title.Trim();
        var body = MarkdownRenderer.ToHtmlFragment(content);

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"zh-CN\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\" />");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        html.Append("<title>").Append(MarkdownRenderer.Escape(heading)).AppendLine("</title>");
        html.AppendLine("<style>");
        html.AppendLine(HtmlStyle);
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.Append("<main class=\"memo\">");
        html.AppendLine();
        html.AppendLine(body.TrimEnd());
        html.AppendLine("</main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private const string HtmlStyle = """
        :root { color-scheme: light; }
        * { box-sizing: border-box; }
        body {
            margin: 0;
            padding: 34px 24px 48px;
            background: #fbf6e4;
            color: #3b382a;
            font-family: "Microsoft YaHei UI", -apple-system, "Segoe UI", "PingFang SC", sans-serif;
            font-size: 15px;
            line-height: 1.7;
        }
        main.memo { max-width: 760px; margin: 0 auto; }
        h1, h2, h3, h4, h5, h6 { margin: 1.3em 0 .5em; line-height: 1.32; }
        h1 { font-size: 1.7em; border-bottom: 1px solid #ded6bd; padding-bottom: .25em; }
        h2 { font-size: 1.38em; border-bottom: 1px solid #e6dfc9; padding-bottom: .2em; }
        h3 { font-size: 1.18em; }
        p { margin: .55em 0; }
        pre.code-block, .formula pre {
            background: #efe8d2;
            border: 1px solid #ded6bd;
            border-radius: 8px;
            padding: 9px 11px;
            overflow-x: auto;
            font-size: .86em;
            line-height: 1.55;
        }
        code { font-family: "Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace; }
        p code, li code, td code, th code { background: #efe8d2; border-radius: 4px; padding: 1px 4px; }
        .formula { margin: .55em 0 1.1em; }
        .formula-tag { display: inline-block; font-size: .72em; font-weight: 600; color: #b27f0a; margin-bottom: 4px; }
        span.formula {
            font-family: "Cascadia Mono", Consolas, monospace;
            background: #efe8d2;
            border-radius: 4px;
            padding: 1px 5px;
            color: #b27f0a;
        }
        blockquote {
            margin: .7em 0 1.1em;
            padding: .2em 0 .2em 12px;
            border-left: 3px solid #b27f0a;
            color: #7c745e;
        }
        table { border-collapse: collapse; margin: .7em 0 1.2em; width: 100%; font-size: .95em; }
        th, td { border: 1px solid #ded6bd; padding: 6px 10px; text-align: left; }
        th { background: #f3ecd6; font-weight: 600; }
        hr { border: none; border-top: 1px solid #ded6bd; margin: 1.5em 0; }
        a { color: #b27f0a; }
        li { margin: .22em 0; }
        """;

    // ==================== 待办 ====================

    /// <summary>Markdown：`- [x] 标题` / `- [ ] 标题`，有截止时间的补在后面。</summary>
    public static string TodosMarkdown(IEnumerable<TodoExportRow> rows)
    {
        var builder = new StringBuilder();

        foreach (var row in rows)
        {
            var title = OneLine(row.Title);
            builder.Append("- ").Append(row.IsDone ? "[x]" : "[ ]").Append(' ');

            // 空标题也要留一条，否则看起来像丢了一行
            builder.Append(title.Length > 0 ? title : "(无标题)");

            if (row.DueAt is { } due)
            {
                builder.Append(" — due ").Append(FormatDue(due));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>CSV：表头「标题,状态,截止,备注」，逗号/引号/换行都按 RFC 4180 转义。</summary>
    public static string TodosCsv(IEnumerable<TodoExportRow> rows)
    {
        var builder = new StringBuilder();
        builder.Append("标题,状态,截止,备注\n");

        foreach (var row in rows)
        {
            builder.Append(CsvField(row.Title)).Append(',')
                .Append(CsvField(row.IsDone ? "已完成" : "未完成")).Append(',')
                .Append(CsvField(row.DueAt is { } due ? FormatDue(due) : string.Empty)).Append(',')
                .Append(CsvField(row.Note)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>JSON：数组，字段 id / title / isDone / dueAt / note / tags。</summary>
    public static string TodosJson(IEnumerable<TodoExportRow> rows)
    {
        var payload = rows.Select(row => new
        {
            id = row.Id,
            title = row.Title,
            isDone = row.IsDone,
            dueAt = row.DueAt is { } due ? FormatDue(due) : null,
            note = row.Note,
            tags = row.Tags,
        });

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>中文别转成 \uXXXX：导出的 JSON 是给人看的。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ==================== 小工具 ====================

    public static string SafeFileName(string? name, string fallback = "board")
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            clean = fallback;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            clean = clean.Replace(invalid, '_');
        }

        clean = clean.Trim().TrimEnd('.');
        if (clean.Length == 0)
        {
            clean = fallback;
        }

        return clean.Length > 60 ? clean[..60] : clean;
    }

    private static string ExtensionOf(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => ".md",
        ExportFormat.Html => ".html",
        ExportFormat.Text => ".txt",
        ExportFormat.Csv => ".csv",
        _ => ".json",
    };

    private static string FilterOf(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => "Markdown (*.md)|*.md|所有文件 (*.*)|*.*",
        ExportFormat.Html => "HTML (*.html)|*.html|所有文件 (*.*)|*.*",
        ExportFormat.Text => "纯文本 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        ExportFormat.Csv => "CSV (*.csv)|*.csv|所有文件 (*.*)|*.*",
        _ => "JSON (*.json)|*.json|所有文件 (*.*)|*.*",
    };

    private static string FormatDue(DateTimeOffset due) =>
        due.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string CsvField(string? value)
    {
        var text = value ?? string.Empty;
        if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string OneLine(string? text) =>
        (text ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static string EnsureTrailingNewline(string? content)
    {
        var text = content ?? string.Empty;
        return text.EndsWith('\n') ? text : text + "\n";
    }
}
