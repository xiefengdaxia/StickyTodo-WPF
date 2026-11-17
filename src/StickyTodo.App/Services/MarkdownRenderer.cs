using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace StickyTodo.App.Services;

/// <summary>
/// 便签的 Markdown 渲染器：纯 WPF，不依赖 WebView2、也不跑任何 JS。
///
/// 两条出口：
///   1. <see cref="ToFlowDocument"/> —— 预览用，产出一份 FlowDocument 交给 FlowDocumentScrollViewer；
///   2. <see cref="ToHtmlFragment"/> —— 导出用，产出 HTML 片段（样式在 ExportService 里统一包）。
///
/// 支持范围：ATX 标题（# ~ ######）、有序/无序列表（含缩进嵌套）、引用、围栏代码块、
/// 管道表格、分隔线，行内 粗体 / 斜体 / 删除线 / 行内代码 / 链接，以及
/// <c>$行内公式$</c>、<c>$$独立公式$$</c>、<c>```mermaid</c> 代码块——
/// 后两者刻意不做真正的排版（没有 MathJax / 没有流程图引擎），
/// 只按「等宽字体 + 淡底色 + 小标签」的代码块样式渲染，让人一眼看出那是公式 / 流程图源码。
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// 便签第一次打开（库里连正文记录都还没有）时预填的示例内容，
    /// 一眼展示标题 / 列表 / 公式 / mermaid 流程图 / 表格都能渲染。
    /// </summary>
    public const string SampleDocument = """
# 便签现在支持 Markdown

切到「预览」就能看到排版效果，切回「编辑」继续写。
正文仍然是**边写边存**的普通文本，随时可以导出成 `.md` / `.html` / `.txt`。

## 今天要做的事

- 把周报写完
- 复盘昨天那个 `Ctrl+X` 的 bug
- 顺手整理一下标签

1. 先看公式怎么写
2. 再看流程图
3. 最后试一下表格

> 提示：编辑模式下 `Ctrl+X / Ctrl+C / Ctrl+V / Ctrl+A / Ctrl+Z / Ctrl+Y` 都能正常用。

## 公式（源码展示）

行内公式 $E = mc^2$ 就夹在句子里。

$$
\int_0^1 x^2 \, dx = \frac{1}{3}
$$

## 流程图（mermaid 源码）

```mermaid
graph TD
    A[打开便签] --> B{要不要写公式}
    B -->|要| C[用 $$ 包起来]
    B -->|不要| D[直接写正文]
    C --> E[切到预览]
    D --> E
    E --> F[导出 Markdown / HTML / 纯文本]
```

## 一张小表格

| 功能 | 状态 | 备注 |
| --- | --- | --- |
| Markdown 预览 | ✅ | 纯 WPF，无浏览器内核 |
| 导出 | ✅ | md / html / txt |
| 新建便签 | ✅ | 标题栏的 ＋ |
| 剪切快捷键 | ✅ | 已修好 |

---

写完这一屏，就可以把它当成真正的便签用了。
""";

    /// <summary>预览渲染用的配色 / 字体。默认值取的是纸感主题，取不到资源也不会崩。</summary>
    public sealed class MarkdownPalette
    {
        public FontFamily BodyFont { get; init; } = new("Microsoft YaHei UI");

        /// <summary>等宽字体：代码块和公式都用它，才能看出「这是源码」。</summary>
        public FontFamily MonoFont { get; init; } = new("Cascadia Mono, Consolas, Microsoft YaHei UI");

        public double FontSize { get; init; } = 13.5;

        public Brush Ink { get; init; } = Solid("#FF3B382A");

        public Brush InkSoft { get; init; } = Solid("#FF7C745E");

        public Brush Accent { get; init; } = Solid("#FFB27F0A");

        public Brush Line { get; init; } = Solid("#243B382A");

        /// <summary>代码块 / 公式块的淡底色。</summary>
        public Brush CodeBackground { get; init; } = Solid("#1F3B382A");

        /// <summary>行内代码的底色（比代码块再淡一点）。</summary>
        public Brush InlineCodeBackground { get; init; } = Solid("#1F3B382A");

        public Brush HeaderBackground { get; init; } = Solid("#14FFFFFF");

        /// <summary>跟随当前皮肤：皮肤换了，预览的颜色也跟着换。</summary>
        public static MarkdownPalette FromResources()
        {
            var fallback = new MarkdownPalette();
            return new MarkdownPalette
            {
                Ink = Resource("Brush.Ink") ?? fallback.Ink,
                InkSoft = Resource("Brush.InkSoft") ?? fallback.InkSoft,
                Accent = Resource("Brush.Accent") ?? fallback.Accent,
                Line = Resource("Brush.Line") ?? fallback.Line,
            };
        }

        private static Brush? Resource(string key) =>
            Application.Current?.TryFindResource(key) as Brush;

        private static Brush Solid(string hex)
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            return brush;
        }
    }

    // ==================== FlowDocument（预览） ====================

    /// <summary>把 Markdown 变成一份可直接塞进 FlowDocumentScrollViewer 的文档。</summary>
    public static FlowDocument ToFlowDocument(string? markdown, MarkdownPalette? palette = null)
    {
        var p = palette ?? MarkdownPalette.FromResources();

        var document = new FlowDocument
        {
            FontFamily = p.BodyFont,
            FontSize = p.FontSize,
            Foreground = p.Ink,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };

        var lines = SplitLines(markdown);
        AddBlocks(document.Blocks, lines, 0, lines.Count, p);

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        }

        return document;
    }

    /// <summary>把文档（或它的选区）取成纯文本，预览模式下 Ctrl+C 用。</summary>
    public static string ToPlainText(FlowDocument? document)
    {
        if (document is null)
        {
            return string.Empty;
        }

        return new TextRange(document.ContentStart, document.ContentEnd).Text.Trim();
    }

    private static void AddBlocks(BlockCollection target, List<string> lines, int start, int end, MarkdownPalette p)
    {
        var i = start;

        while (i < end)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // ``` / ~~~ 围栏代码块（含 mermaid）
            if (TryFenceStart(trimmed, out var fence, out var language))
            {
                var code = new List<string>();
                i++;
                while (i < end && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                if (i < end)
                {
                    i++;
                }

                AddCodeBlock(target, language, code, p);
                continue;
            }

            // $$ 独立公式块
            if (trimmed.StartsWith("$$", StringComparison.Ordinal))
            {
                i = AddFormulaBlock(target, lines, i, end, p);
                continue;
            }

            if (TryHeading(trimmed, out var level, out var headingText))
            {
                target.Add(BuildHeading(headingText, level, p));
                i++;
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                target.Add(BuildHorizontalRule(p));
                i++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                i = AddBlockquote(target, lines, i, end, p);
                continue;
            }

            if (IsTableStart(lines, i))
            {
                i = AddTable(target, lines, i, end, p);
                continue;
            }

            if (TryParseListItem(lines[i], out _, out _, out _, out _))
            {
                i = AddList(target, lines, i, end, p);
                continue;
            }

            i = AddParagraph(target, lines, i, end, p);
        }
    }

    private static Paragraph BuildHeading(string text, int level, MarkdownPalette p)
    {
        var size = level switch
        {
            1 => 19d,
            2 => 16.5d,
            3 => 14.5d,
            4 => 13.5d,
            5 => 13d,
            _ => 12.5d,
        };

        var paragraph = new Paragraph
        {
            FontSize = size,
            FontWeight = FontWeights.Bold,
            Foreground = p.Ink,
            Margin = new Thickness(0, level == 1 ? 2 : 10, 0, 6),
        };

        // 一级 / 二级标题下面压一条线，层级一眼看得出来
        if (level <= 2)
        {
            paragraph.BorderBrush = p.Line;
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 3);
        }

        AppendInlines(paragraph.Inlines, text, p, InlineStyle.Normal);
        return paragraph;
    }

    private static Paragraph BuildHorizontalRule(MarkdownPalette p) => new()
    {
        Margin = new Thickness(0, 10, 0, 12),
        BorderBrush = p.Line,
        BorderThickness = new Thickness(0, 1, 0, 0),
        FontSize = 1,
    };

    /// <summary>代码块 / 公式块 / mermaid 块：等宽 + 淡底 + 左上角小标签。</summary>
    private static void AddCodeBlock(BlockCollection target, string? language, List<string> code, MarkdownPalette p)
    {
        var lang = (language ?? string.Empty).Trim().ToLowerInvariant();
        var isFormula = lang is "math" or "latex" or "tex" or "katex";
        var isMermaid = lang is "mermaid" or "flow" or "flowchart" or "graph";

        var label = isFormula
            ? "公式"
            : isMermaid
                ? "流程图 · mermaid"
                : lang.Length > 0
                    ? lang
                    : null;

        var section = new Section
        {
            Background = p.CodeBackground,
            BorderBrush = isFormula || isMermaid ? p.Accent : p.Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 6, 9, 8),
            Margin = new Thickness(0, 4, 0, 10),
        };

        if (label is not null)
        {
            section.Blocks.Add(new Paragraph(new Run(label))
            {
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = isFormula || isMermaid ? p.Accent : p.InkSoft,
                Margin = new Thickness(0, 0, 0, 3),
            });
        }

        if (code.Count == 0)
        {
            code.Add(string.Empty);
        }

        // 流程图源码带行号更好读（mermaid 的节点名一对不上就难查）
        var numbered = isMermaid;

        for (var index = 0; index < code.Count; index++)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                FontFamily = p.MonoFont,
                FontSize = 11.5,
                Foreground = p.Ink,
            };

            if (numbered)
            {
                paragraph.Inlines.Add(new Run($"{index + 1,2}  ")
                {
                    Foreground = p.InkSoft,
                    FontSize = 10,
                    FontFamily = p.MonoFont,
                });
            }

            paragraph.Inlines.Add(new Run(code[index].Length == 0 ? " " : code[index]));
            section.Blocks.Add(paragraph);
        }

        target.Add(section);
    }

    private static int AddFormulaBlock(BlockCollection target, List<string> lines, int start, int end, MarkdownPalette p)
    {
        var first = lines[start].Trim();
        var inner = first.Length > 2 ? first[2..] : string.Empty;
        var code = new List<string>();

        // $$ 独占一行、内容也在同一行：$$E=mc^2$$
        if (inner.EndsWith("$$", StringComparison.Ordinal) && inner.Length >= 2)
        {
            code.Add(inner[..^2].Trim());
            AddCodeBlock(target, "math", code, p);
            return start + 1;
        }

        if (inner.Trim().Length > 0)
        {
            code.Add(inner.TrimEnd());
        }

        var i = start + 1;
        while (i < end)
        {
            var line = lines[i];
            var close = line.IndexOf("$$", StringComparison.Ordinal);
            if (close >= 0)
            {
                var head = line[..close].TrimEnd();
                if (head.Length > 0)
                {
                    code.Add(head);
                }

                i++;
                break;
            }

            code.Add(line);
            i++;
        }

        AddCodeBlock(target, "math", code, p);
        return i;
    }

    private static int AddBlockquote(BlockCollection target, List<string> lines, int start, int end, MarkdownPalette p)
    {
        var inner = new List<string>();
        var i = start;

        while (i < end)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0)
            {
                // 引用里的空行：下一行还是引用才继续
                if (i + 1 < end && lines[i + 1].TrimStart().StartsWith('>'))
                {
                    inner.Add(string.Empty);
                    i++;
                    continue;
                }

                break;
            }

            if (!trimmed.StartsWith('>'))
            {
                break;
            }

            var text = trimmed[1..];
            inner.Add(text.StartsWith(' ') ? text[1..] : text);
            i++;
        }

        var section = new Section
        {
            BorderBrush = p.Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 2, 0, 10),
            Foreground = p.InkSoft,
        };

        AddBlocks(section.Blocks, inner, 0, inner.Count, p);

        if (section.Blocks.Count == 0)
        {
            section.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        }

        target.Add(section);
        return i;
    }

    private static int AddTable(BlockCollection target, List<string> lines, int start, int end, MarkdownPalette p)
    {
        var header = SplitTableRow(lines[start]);
        var i = start + 2;

        var body = new List<List<string>>();
        while (i < end)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || !line.Contains('|') || IsHorizontalRule(line.Trim()))
            {
                break;
            }

            body.Add(SplitTableRow(line));
            i++;
        }

        var columns = header.Count;
        foreach (var row in body)
        {
            columns = Math.Max(columns, row.Count);
        }

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 10),
        };

        for (var c = 0; c < columns; c++)
        {
            table.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        group.Rows.Add(BuildTableRow(header, columns, p, isHeader: true));

        foreach (var row in body)
        {
            group.Rows.Add(BuildTableRow(row, columns, p, isHeader: false));
        }

        target.Add(table);
        return i;
    }

    private static TableRow BuildTableRow(List<string> cells, int columns, MarkdownPalette p, bool isHeader)
    {
        var row = new TableRow();
        if (isHeader)
        {
            row.FontWeight = FontWeights.SemiBold;
        }

        for (var c = 0; c < columns; c++)
        {
            var cell = new TableCell
            {
                BorderBrush = p.Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 4, 7, 4),
                Background = isHeader ? p.HeaderBackground : null,
            };

            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                FontSize = isHeader ? 12 : 12.5,
            };

            AppendInlines(paragraph.Inlines, c < cells.Count ? cells[c] : string.Empty, p, InlineStyle.Normal);
            cell.Blocks.Add(paragraph);
            row.Cells.Add(cell);
        }

        return row;
    }

    private sealed record ListLevel(int Indent, bool Ordered, List List);

    private static int AddList(BlockCollection target, List<string> lines, int start, int end, MarkdownPalette p)
    {
        var levels = new List<ListLevel>();
        var i = start;

        while (i < end)
        {
            if (lines[i].Trim().Length == 0)
            {
                i++;
                continue;
            }

            if (!TryParseListItem(lines[i], out var indent, out var ordered, out var number, out var text))
            {
                break;
            }

            // 缩进回退：把已经结束的层收掉
            while (levels.Count > 0 && indent < levels[^1].Indent)
            {
                levels.RemoveAt(levels.Count - 1);
            }

            // 同一缩进但有序/无序换了：换成并列的另一个列表
            if (levels.Count > 0 && indent == levels[^1].Indent && ordered != levels[^1].Ordered)
            {
                levels.RemoveAt(levels.Count - 1);
            }

            if (levels.Count == 0 || indent > levels[^1].Indent)
            {
                var depth = levels.Count;
                var list = new List
                {
                    MarkerStyle = ordered
                        ? TextMarkerStyle.Decimal
                        : depth == 0
                            ? TextMarkerStyle.Disc
                            : depth == 1
                                ? TextMarkerStyle.Circle
                                : TextMarkerStyle.Square,
                    Margin = new Thickness(0, 0, 0, depth == 0 ? 8 : 3),
                    Padding = new Thickness(depth == 0 ? 22 : 18, 0, 0, 0),
                    MarkerOffset = 6,
                    StartIndex = ordered ? Math.Max(1, number) : 1,
                };

                if (levels.Count == 0)
                {
                    target.Add(list);
                }
                else
                {
                    var parent = levels[^1].List;
                    ListItem owner;
                    if (parent.ListItems.Count == 0)
                    {
                        owner = new ListItem(new Paragraph());
                        parent.ListItems.Add(owner);
                    }
                    else
                    {
                        owner = parent.ListItems.Last();
                    }

                    owner.Blocks.Add(list);
                }

                levels.Add(new ListLevel(indent, ordered, list));
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
            AppendInlines(paragraph.Inlines, text, p, InlineStyle.Normal);
            levels[^1].List.ListItems.Add(new ListItem(paragraph));
            i++;
        }

        return i;
    }

    private static int AddParagraph(BlockCollection target, List<string> lines, int start, int end, MarkdownPalette p)
    {
        var buffer = new List<string>();
        var i = start;

        while (i < end)
        {
            if (lines[i].Trim().Length == 0)
            {
                break;
            }

            if (buffer.Count > 0 && StartsNewBlock(lines, i))
            {
                break;
            }

            buffer.Add(lines[i].Trim());
            i++;
        }

        if (buffer.Count == 0)
        {
            buffer.Add(string.Empty);
            i = Math.Min(start + 1, end);
        }

        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
        AppendInlines(paragraph.Inlines, string.Join("\n", buffer), p, InlineStyle.Normal);
        target.Add(paragraph);
        return i;
    }

    private static bool StartsNewBlock(List<string> lines, int index)
    {
        var trimmed = lines[index].Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (TryFenceStart(trimmed, out _, out _) ||
            trimmed.StartsWith("$$", StringComparison.Ordinal) ||
            TryHeading(trimmed, out _, out _) ||
            IsHorizontalRule(trimmed) ||
            trimmed.StartsWith('>') ||
            IsTableStart(lines, index))
        {
            return true;
        }

        return TryParseListItem(lines[index], out _, out _, out _, out _);
    }

    // ==================== 行内格式 ====================

    private readonly record struct InlineStyle(FontWeight Weight, bool Italic, bool Strike)
    {
        public static InlineStyle Normal => new(FontWeights.Normal, false, false);

        public InlineStyle WithBold() => this with { Weight = FontWeights.Bold };

        public InlineStyle WithItalic() => this with { Italic = true };

        public InlineStyle WithStrike() => this with { Strike = true };
    }

    private static void AppendInlines(InlineCollection target, string text, MarkdownPalette p, InlineStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var literal = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (literal.Length == 0)
            {
                return;
            }

            target.Add(Styled(new Run(literal.ToString()), style));
            literal.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                literal.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\n')
            {
                Flush();
                target.Add(new LineBreak());
                i++;
                continue;
            }

            if (c == '`')
            {
                var ticks = CountRun(text, i, '`');
                var fence = new string('`', ticks);
                var close = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);

                if (close > i + ticks)
                {
                    Flush();
                    target.Add(CodeInline(text[(i + ticks)..close], p));
                    i = close + ticks;
                    continue;
                }

                literal.Append(fence);
                i += ticks;
                continue;
            }

            if (c == '$')
            {
                var close = text.IndexOf('$', i + 1);
                if (close > i + 1 && text[(i + 1)..close].Trim().Length > 0)
                {
                    Flush();
                    target.Add(FormulaInline(text[(i + 1)..close], p));
                    i = close + 1;
                    continue;
                }

                literal.Append(c);
                i++;
                continue;
            }

            if (c == '[')
            {
                if (TryLink(text, i, out var label, out var url, out var next))
                {
                    Flush();
                    target.Add(LinkInline(label, url, p, style));
                    i = next;
                    continue;
                }

                literal.Append(c);
                i++;
                continue;
            }

            if (c is '*' or '_' or '~')
            {
                var run = CountRun(text, i, c);

                if (c == '~' && run < 2)
                {
                    literal.Append(c);
                    i++;
                    continue;
                }

                var marker = c == '~' ? "~~" : new string(c, Math.Min(run, 3));

                if (TryDelimited(text, i, marker, out var inner, out var next))
                {
                    Flush();

                    var child = c == '~'
                        ? style.WithStrike()
                        : marker.Length == 3
                            ? style.WithBold().WithItalic()
                            : marker.Length == 2
                                ? style.WithBold()
                                : style.WithItalic();

                    AppendInlines(target, inner, p, child);
                    i = next;
                    continue;
                }

                literal.Append(marker);
                i += marker.Length;
                continue;
            }

            literal.Append(c);
            i++;
        }

        Flush();
    }

    private static Run Styled(Run run, InlineStyle style)
    {
        if (style.Weight != FontWeights.Normal)
        {
            run.FontWeight = style.Weight;
        }

        if (style.Italic)
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (style.Strike)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }

        return run;
    }

    private static Run CodeInline(string code, MarkdownPalette p) => new(code)
    {
        FontFamily = p.MonoFont,
        FontSize = Math.Max(9, p.FontSize - 1),
        Background = p.InlineCodeBackground,
        Foreground = p.Ink,
    };

    private static Run FormulaInline(string formula, MarkdownPalette p) => new($" {formula.Trim()} ")
    {
        FontFamily = p.MonoFont,
        FontSize = Math.Max(9, p.FontSize - 1.5),
        Background = p.CodeBackground,
        Foreground = p.Accent,
    };

    private static Inline LinkInline(string label, string url, MarkdownPalette p, InlineStyle style)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
        {
            var hyperlink = new Hyperlink
            {
                Foreground = p.Accent,
                TextDecorations = TextDecorations.Underline,
                ToolTip = url,
                Command = new OpenUrlCommand(uri),
            };

            AppendInlines(hyperlink.Inlines, label.Length == 0 ? url : label, p, style);
            return hyperlink;
        }

        // 不是能点的链接就老实显示成文本，别给个点了没反应的蓝色字
        return Styled(new Run($"{label} ({url})"), style);
    }

    /// <summary>点预览里的链接：交给系统默认浏览器打开（只认 http/https/mailto）。</summary>
    private sealed class OpenUrlCommand : ICommand
    {
        private readonly Uri _uri;

        public OpenUrlCommand(Uri uri) => _uri = uri;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => OpenExternal(_uri);
    }

    /// <summary>用系统默认程序打开一个 URL；失败只记日志，不打扰用户。</summary>
    public static void OpenExternal(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[markdown] 打不开链接 {Url}", uri.AbsoluteUri);
        }
    }

    // ==================== HTML（导出） ====================

    /// <summary>把 Markdown 变成 HTML 片段（不含 &lt;html&gt; 外壳，外壳在 ExportService 里包）。</summary>
    public static string ToHtmlFragment(string? markdown)
    {
        var lines = SplitLines(markdown);
        var html = new StringBuilder();
        var i = 0;

        while (i < lines.Count)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (TryFenceStart(trimmed, out var fence, out var language))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Count && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                if (i < lines.Count)
                {
                    i++;
                }

                var lang = language.ToLowerInvariant();
                var css = lang.Length == 0
                    ? string.Empty
                    : $" class=\"language-{Escape(lang)}\"";

                html.Append("<pre class=\"code-block\"><code").Append(css).Append('>')
                    .Append(Escape(string.Join("\n", code)))
                    .AppendLine("</code></pre>");
                continue;
            }

            if (trimmed.StartsWith("$$", StringComparison.Ordinal))
            {
                var inner = trimmed.Length > 2 ? trimmed[2..] : string.Empty;
                var code = new List<string>();

                if (inner.EndsWith("$$", StringComparison.Ordinal) && inner.Length >= 2)
                {
                    code.Add(inner[..^2].Trim());
                    i++;
                }
                else
                {
                    if (inner.Trim().Length > 0)
                    {
                        code.Add(inner.TrimEnd());
                    }

                    i++;
                    while (i < lines.Count)
                    {
                        var close = lines[i].IndexOf("$$", StringComparison.Ordinal);
                        if (close >= 0)
                        {
                            var head = lines[i][..close].TrimEnd();
                            if (head.Length > 0)
                            {
                                code.Add(head);
                            }

                            i++;
                            break;
                        }

                        code.Add(lines[i]);
                        i++;
                    }
                }

                html.Append("<div class=\"formula\"><span class=\"formula-tag\">公式</span><pre><code>")
                    .Append(Escape(string.Join("\n", code)))
                    .AppendLine("</code></pre></div>");
                continue;
            }

            if (TryHeading(trimmed, out var level, out var headingText))
            {
                html.Append('<').Append('h').Append(level).Append('>')
                    .Append(InlineToHtml(headingText))
                    .Append("</h").Append(level).AppendLine(">");
                i++;
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                html.AppendLine("<hr />");
                i++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var inner = new List<string>();
                while (i < lines.Count && lines[i].TrimStart().StartsWith('>'))
                {
                    var text = lines[i].TrimStart()[1..];
                    inner.Add(text.StartsWith(' ') ? text[1..] : text);
                    i++;
                }

                html.Append("<blockquote>")
                    .Append(ToHtmlFragment(string.Join("\n", inner)))
                    .AppendLine("</blockquote>");
                continue;
            }

            if (IsTableStart(lines, i))
            {
                var header = SplitTableRow(lines[i]);
                i += 2;

                var body = new List<List<string>>();
                while (i < lines.Count &&
                       lines[i].Trim().Length > 0 &&
                       lines[i].Contains('|') &&
                       !IsHorizontalRule(lines[i].Trim()))
                {
                    body.Add(SplitTableRow(lines[i]));
                    i++;
                }

                html.AppendLine("<table><thead><tr>");
                foreach (var cell in header)
                {
                    html.Append("<th>").Append(InlineToHtml(cell)).AppendLine("</th>");
                }

                html.AppendLine("</tr></thead><tbody>");
                foreach (var row in body)
                {
                    html.AppendLine("<tr>");
                    foreach (var cell in row)
                    {
                        html.Append("<td>").Append(InlineToHtml(cell)).AppendLine("</td>");
                    }

                    html.AppendLine("</tr>");
                }

                html.AppendLine("</tbody></table>");
                continue;
            }

            if (TryParseListItem(lines[i], out _, out _, out _, out _))
            {
                var stack = new List<(int Indent, bool Ordered)>();

                while (i < lines.Count && TryParseListItem(lines[i], out var indent, out var ordered, out _, out var text))
                {
                    while (stack.Count > 0 && indent < stack[^1].Indent)
                    {
                        html.Append("</li>").AppendLine(stack[^1].Ordered ? "</ol>" : "</ul>");
                        stack.RemoveAt(stack.Count - 1);
                    }

                    if (stack.Count > 0 && indent == stack[^1].Indent && ordered != stack[^1].Ordered)
                    {
                        html.Append("</li>").AppendLine(stack[^1].Ordered ? "</ol>" : "</ul>");
                        stack.RemoveAt(stack.Count - 1);
                    }

                    if (stack.Count == 0 || indent > stack[^1].Indent)
                    {
                        html.AppendLine(ordered ? "<ol>" : "<ul>");
                        stack.Add((indent, ordered));
                    }
                    else
                    {
                        html.AppendLine("</li>");
                    }

                    html.Append("<li>").AppendLine(InlineToHtml(text));
                    i++;
                }

                while (stack.Count > 0)
                {
                    html.Append("</li>").AppendLine(stack[^1].Ordered ? "</ol>" : "</ul>");
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            // 段落
            var buffer = new List<string>();
            while (i < lines.Count && lines[i].Trim().Length > 0 && (buffer.Count == 0 || !StartsNewBlock(lines, i)))
            {
                buffer.Add(lines[i].Trim());
                i++;
            }

            if (buffer.Count == 0)
            {
                i++;
                continue;
            }

            html.Append("<p>").Append(InlineToHtml(string.Join("\n", buffer))).AppendLine("</p>");
        }

        return html.ToString();
    }

    /// <summary>行内 Markdown → HTML（同样只覆盖预览支持的那些标记）。</summary>
    public static string InlineToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        var literal = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (literal.Length == 0)
            {
                return;
            }

            html.Append(Escape(literal.ToString()));
            literal.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                literal.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\n')
            {
                Flush();
                html.Append("<br />");
                i++;
                continue;
            }

            if (c == '`')
            {
                var ticks = CountRun(text, i, '`');
                var fence = new string('`', ticks);
                var close = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);

                if (close > i + ticks)
                {
                    Flush();
                    html.Append("<code>").Append(Escape(text[(i + ticks)..close])).Append("</code>");
                    i = close + ticks;
                    continue;
                }

                literal.Append(fence);
                i += ticks;
                continue;
            }

            if (c == '$')
            {
                var close = text.IndexOf('$', i + 1);
                if (close > i + 1 && text[(i + 1)..close].Trim().Length > 0)
                {
                    Flush();
                    html.Append("<span class=\"formula\">")
                        .Append(Escape(text[(i + 1)..close].Trim()))
                        .Append("</span>");
                    i = close + 1;
                    continue;
                }

                literal.Append(c);
                i++;
                continue;
            }

            if (c == '[' && TryLink(text, i, out var label, out var url, out var next))
            {
                Flush();
                html.Append("<a href=\"").Append(Escape(url)).Append("\">")
                    .Append(InlineToHtml(label.Length == 0 ? url : label))
                    .Append("</a>");
                i = next;
                continue;
            }

            if (c is '*' or '_' or '~')
            {
                var run = CountRun(text, i, c);

                if (c == '~' && run < 2)
                {
                    literal.Append(c);
                    i++;
                    continue;
                }

                var marker = c == '~' ? "~~" : new string(c, Math.Min(run, 3));

                if (TryDelimited(text, i, marker, out var inner, out var close))
                {
                    Flush();

                    var tag = c == '~'
                        ? "del"
                        : marker.Length == 3
                            ? "strong"
                            : marker.Length == 2
                                ? "strong"
                                : "em";

                    var payload = marker.Length == 3
                        ? $"<em>{InlineToHtml(inner)}</em>"
                        : InlineToHtml(inner);

                    html.Append('<').Append(tag).Append('>').Append(payload).Append("</").Append(tag).Append('>');
                    i = close;
                    continue;
                }

                literal.Append(marker);
                i += marker.Length;
                continue;
            }

            literal.Append(c);
            i++;
        }

        Flush();
        return html.ToString();
    }

    /// <summary>HTML 转义（&lt; &gt; &amp; "）。</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    // ==================== 词法小工具 ====================

    private static List<string> SplitLines(string? markdown)
    {
        var normalized = (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        return normalized.Split('\n').ToList();
    }

    private static bool TryFenceStart(string trimmed, out string fence, out string language)
    {
        fence = string.Empty;
        language = string.Empty;

        var backtick = trimmed.StartsWith("```", StringComparison.Ordinal);
        var tilde = trimmed.StartsWith("~~~", StringComparison.Ordinal);

        if (!backtick && !tilde)
        {
            return false;
        }

        fence = backtick ? "```" : "~~~";
        language = trimmed[fence.Length..].Trim();

        // ```csharp {.line-numbers} 这类附加信息只要语言名
        var cut = language.IndexOfAny(new[] { ' ', '{', ',' });
        if (cut > 0)
        {
            language = language[..cut];
        }

        return true;
    }

    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == '#')
        {
            count++;
        }

        if (count is 0 or > 6)
        {
            return false;
        }

        // "#标题" 不算标题（CommonMark 一致）
        if (count < trimmed.Length && trimmed[count] != ' ')
        {
            return false;
        }

        level = count;
        text = count >= trimmed.Length ? string.Empty : trimmed[count..].Trim().TrimEnd('#').Trim();
        return true;
    }

    private static bool IsHorizontalRule(string trimmed)
    {
        if (trimmed.Length < 3)
        {
            return false;
        }

        var marker = trimmed[0];
        if (marker is not ('-' or '*' or '_'))
        {
            return false;
        }

        var count = 0;
        foreach (var c in trimmed)
        {
            if (c == marker)
            {
                count++;
                continue;
            }

            if (c == ' ')
            {
                continue;
            }

            return false;
        }

        return count >= 3;
    }

    private static bool IsTableStart(List<string> lines, int index)
    {
        if (index + 1 >= lines.Count)
        {
            return false;
        }

        if (!lines[index].Contains('|'))
        {
            return false;
        }

        var separator = lines[index + 1].Trim();
        if (separator.Length == 0 || !separator.Contains('|'))
        {
            return false;
        }

        var cells = SplitTableRow(separator);
        return cells.Count > 0 && cells.All(IsTableSeparatorCell);
    }

    private static bool IsTableSeparatorCell(string cell)
    {
        var text = cell.Trim().Trim(':');
        return text.Length >= 1 && text.All(c => c == '-');
    }

    private static List<string> SplitTableRow(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }

        if (text.EndsWith('|'))
        {
            text = text[..^1];
        }

        var cells = new List<string>();
        var current = new StringBuilder();
        var escaped = false;

        foreach (var c in text)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static bool TryParseListItem(string line, out int indent, out bool ordered, out int number, out string text)
    {
        indent = 0;
        ordered = false;
        number = 1;
        text = string.Empty;

        var spaces = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                spaces++;
                continue;
            }

            if (c == '\t')
            {
                spaces += 4;
                continue;
            }

            break;
        }

        var trimmed = line.TrimStart();
        if (trimmed.Length < 2)
        {
            return false;
        }

        var marker = trimmed[0];

        if (marker is '-' or '*' or '+')
        {
            if (trimmed[1] != ' ')
            {
                return false;
            }

            indent = spaces;
            ordered = false;
            text = trimmed[2..].Trim();
            return true;
        }

        if (!char.IsDigit(marker))
        {
            return false;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits + 1 >= trimmed.Length)
        {
            return false;
        }

        var delimiter = trimmed[digits];
        if (delimiter is not ('.' or ')'))
        {
            return false;
        }

        if (trimmed[digits + 1] != ' ')
        {
            return false;
        }

        indent = spaces;
        ordered = true;
        number = int.TryParse(trimmed[..digits], out var parsed) ? parsed : 1;
        text = trimmed[(digits + 2)..].Trim();
        return true;
    }

    private static bool TryLink(string text, int start, out string label, out string url, out int next)
    {
        label = string.Empty;
        url = string.Empty;
        next = start;

        var closeBracket = text.IndexOf(']', start + 1);
        if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
        {
            return false;
        }

        var closeParen = text.IndexOf(')', closeBracket + 2);
        if (closeParen < 0)
        {
            return false;
        }

        label = text[(start + 1)..closeBracket];
        url = text[(closeBracket + 2)..closeParen].Trim();
        next = closeParen + 1;
        return url.Length > 0;
    }

    private static bool TryDelimited(string text, int start, string marker, out string inner, out int next)
    {
        inner = string.Empty;
        next = start;

        var open = start + marker.Length;
        if (open >= text.Length || char.IsWhiteSpace(text[open]))
        {
            return false;
        }

        var close = text.IndexOf(marker, open, StringComparison.Ordinal);
        if (close <= open || char.IsWhiteSpace(text[close - 1]))
        {
            return false;
        }

        inner = text[open..close];
        next = close + marker.Length;
        return true;
    }

    private static int CountRun(string text, int start, char c)
    {
        var count = 0;
        while (start + count < text.Length && text[start + count] == c)
        {
            count++;
        }

        return count;
    }

    private static bool IsEscapable(char c) =>
        c is '\\' or '`' or '*' or '_' or '[' or ']' or '(' or ')' or '#' or '-' or '>' or '$' or '~' or '|';
}
