using System.Text.Json;

namespace StickyTodo.Mcp.Market;

/// <summary>市场里的一个看板（一个文件夹 = 一个看板）。</summary>
public sealed record MarketBoard(
    string Id,
    string Name,
    string Summary,
    string? Description,
    string Author,
    string Version,
    int? IntervalSeconds,
    IReadOnlyList<string> Tags,
    string Html,
    string? Script,
    string? PreviewPath,
    string Directory);

/// <summary>
/// 看板市场：读取仓库 <c>market/</c> 目录下的看板包。
///
/// 目录结构（一个看板一个文件夹）：
/// <code>
/// market/price-calc/
///   board.json     ← 清单：id / name / summary / tags / intervalSeconds / html / script / preview
///   board.html     ← 页面（必需）
///   board.js       ← 采集脚本（可选）
///   preview.png    ← 预览图（可选）
/// </code>
///
/// 目录解析顺序：环境变量 STICKYTODO_MARKET_DIR → 从 exe 目录逐级向上找 market/。
/// 这样开发目录（bin\Release\net8.0-windows）和发布产物（exe 旁边）都能找到。
/// </summary>
public static class BoardMarket
{
    public const string FolderName = "market";
    public const string ManifestName = "board.json";
    public const string DirectoryEnvironmentVariable = "STICKYTODO_MARKET_DIR";
    public const int MaxHtmlBytes = 1_000_000;

    /// <summary>找到 market 目录的绝对路径；找不到返回 null。</summary>
    public static string? ResolveDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, FolderName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>读取全部看板；单个包有问题就跳过，不影响其它包。</summary>
    public static IReadOnlyList<MarketBoard> Load(string? directory = null)
    {
        var boards = new List<MarketBoard>();
        directory ??= ResolveDirectory();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return boards;
        }

        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.EnumerateFiles(directory, ManifestName, SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return boards;
        }

        foreach (var manifest in manifests.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var board = TryLoadOne(manifest);
            if (board is not null)
            {
                boards.Add(board);
            }
        }

        boards.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return boards;
    }

    private static MarketBoard? TryLoadOne(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var folder = Path.GetDirectoryName(manifestPath)!;

            string Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

            var htmlFile = Text("html");
            if (htmlFile.Length == 0)
            {
                htmlFile = "board.html";
            }

            var htmlPath = Path.Combine(folder, htmlFile);
            if (!File.Exists(htmlPath))
            {
                return null;
            }

            var html = File.ReadAllText(htmlPath);

            // 按字节算：中文/emoji 页面字符数远小于字节数，只卡字符数会放进超大页面。
            if (System.Text.Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes)
            {
                return null;
            }

            string? script = null;
            var scriptFile = Text("script");
            if (scriptFile.Length > 0)
            {
                var scriptPath = Path.Combine(folder, scriptFile);
                if (File.Exists(scriptPath))
                {
                    script = File.ReadAllText(scriptPath);
                }
            }

            string? preview = null;
            var previewFile = Text("preview");
            if (previewFile.Length > 0)
            {
                var previewPath = Path.Combine(folder, previewFile);
                if (File.Exists(previewPath))
                {
                    preview = previewPath;
                }
            }

            var id = Text("id");
            if (id.Length == 0)
            {
                id = Path.GetFileName(folder);
            }

            var name = Text("name");
            if (name.Length == 0)
            {
                name = id;
            }

            int? interval = null;
            if (root.TryGetProperty("intervalSeconds", out var intervalValue) &&
                intervalValue.ValueKind == JsonValueKind.Number &&
                intervalValue.TryGetInt32(out var seconds))
            {
                interval = seconds;
            }

            var tags = new List<string>();
            if (root.TryGetProperty("tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagArray.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { Length: > 0 } text)
                    {
                        tags.Add(text);
                    }
                }
            }

            return new MarketBoard(
                id,
                name,
                Text("summary"),
                Text("description"),
                Text("author"),
                Text("version"),
                interval,
                tags,
                html,
                script,
                preview,
                folder);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
