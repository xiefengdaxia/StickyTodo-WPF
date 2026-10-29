using System.Text;

namespace StickyTodo.Updates;

/// <summary>
/// 把 GitHub release 的 Markdown 说明压成「界面能直接显示的纯文本」。
///
/// 只做最保守的处理：去掉标题井号、把 -/* 列表换成圆点、丢掉代码块围栏，
/// 其余原样保留。不渲染 HTML，也不下载任何图片。
/// </summary>
public static class ReleaseNotes
{
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(markdown.Length);
        var previousBlank = false;

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                line = trimmed[4..];
            }
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                line = trimmed[3..];
            }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                line = trimmed[2..];
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                     || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                line = "• " + trimmed[2..];
            }

            if (line.Length == 0)
            {
                // 连续空行压成一行，弹窗里不至于空一大片
                if (previousBlank)
                {
                    continue;
                }

                previousBlank = true;
            }
            else
            {
                previousBlank = false;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().Trim();
    }
}
