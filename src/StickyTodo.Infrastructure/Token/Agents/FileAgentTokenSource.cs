using System.Text;
using System.Text.Json;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.Infrastructure.Token.Agents;
/// <summary>
/// Agent 数据源的公共基类：文件发现 + 按天缓存。
/// 各客户端只实现 <see cref="ParseFile"/>（把一个会话文件拆成调用记录）。
///
/// <para><b>缓存语义（改之前先读）</b>：<see cref="ParseFile"/> 必须返回文件的<b>全部</b>记录、
/// 不要按日期截断——文件内容按 mtime 缓存，如果在这里按首次查询的起点截断，
/// 之后查更宽的区间时 mtime 没变、缓存命中，老记录就永远丢了。日期过滤只在
/// <see cref="LoadDays"/> 里按"这次缺哪些天"做。</para>
/// </summary>
internal abstract class FileAgentTokenSource : IAgentTokenSource
{
    protected abstract string AgentIdCore { get; }
    protected abstract string DisplayNameCore { get; }
    protected abstract string SourceDescriptionCore { get; }

    /// <summary>要扫描的文件集合（返回空 = 客户端没装 / 没数据）。</summary>
    protected abstract IReadOnlyList<SourceFile> EnumerateFiles();

    /// <summary>解析一个文件，产出它的全部调用记录（不做日期过滤）。</summary>
    protected abstract IEnumerable<AgentUsageEntry> ParseFile(SourceFile file);

    public string AgentId => AgentIdCore;
    public string DisplayName => DisplayNameCore;
    public string SourceDescription => SourceDescriptionCore;

    public bool IsAvailable
    {
        get
        {
            try
            {
                return EnumerateFiles().Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>已解析文件缓存：路径 → (mtimeTicks, 记录)。mtime 没变直接复用。</summary>
    private readonly Dictionary<string, (long Mtime, List<(DateOnly Day, AgentUsageEntry Entry)> Records)> _fileCache = new();

    /// <summary>按天缓存的聚合：日期 → 记录。历史日期不变，只重算今天。</summary>
    private readonly Dictionary<DateOnly, List<AgentUsageEntry>> _dayCache = new();

    public IEnumerable<AgentUsageEntry> ReadEntries(DateOnly fromDay)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (fromDay > today)
        {
            return [];
        }

        // 需要重算的日子：今天永远重算（还在写），历史日缺缓存才算
        var missing = new List<DateOnly>();
        for (var day = fromDay; day <= today; day = day.AddDays(1))
        {
            if (day == today || !_dayCache.ContainsKey(day))
            {
                missing.Add(day);
            }
        }

        if (missing.Count > 0)
        {
            LoadDays(missing);
        }

        var result = new List<AgentUsageEntry>();
        for (var day = fromDay; day <= today; day = day.AddDays(1))
        {
            if (_dayCache.TryGetValue(day, out var entries))
            {
                result.AddRange(entries);
            }
        }

        return result;
    }

    private void LoadDays(List<DateOnly> days)
    {
        var minDay = days.Min();
        var fresh = new Dictionary<DateOnly, List<AgentUsageEntry>>();

        foreach (var file in EnumerateFiles())
        {
            List<(DateOnly Day, AgentUsageEntry Entry)> records;
            try
            {
                var mtime = File.GetLastWriteTimeUtc(file.Path).Ticks;
                if (_fileCache.TryGetValue(file.Path, out var cached) && cached.Mtime == mtime)
                {
                    records = cached.Records;
                }
                else
                {
                    records = [];
                    try
                    {
                        foreach (var entry in ParseFile(file))
                        {
                            records.Add((DateOnly.FromDateTime(entry.Timestamp.LocalDateTime), entry));
                        }
                    }
                    catch (IOException)
                    {
                        // 客户端正在写的文件可能瞬间读不到，跳过等下轮
                    }
                    catch (Exception ex) when (ex is JsonException or FormatException)
                    {
                        // 半行/坏行由 ParseFile 内部吞掉；整文件坏掉也只丢这一个文件
                        records = [];
                    }

                    _fileCache[file.Path] = (mtime, records);
                }
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var (day, entry) in records)
            {
                if (day < minDay || !days.Contains(day))
                {
                    continue;
                }

                if (!fresh.TryGetValue(day, out var list))
                {
                    list = [];
                    fresh[day] = list;
                }

                list.Add(entry);
            }
        }

        foreach (var day in days)
        {
            _dayCache[day] = fresh.TryGetValue(day, out var value) ? value : [];
        }

        // 文件缓存只留最近 400 个（沿用 magpie 的预算思路），防止长期驻留膨胀
        if (_fileCache.Count > 400)
        {
            foreach (var path in _fileCache.Keys.Take(_fileCache.Count - 400).ToList())
            {
                _fileCache.Remove(path);
            }
        }
    }

    /// <summary>一个待解析的会话文件。</summary>
    protected sealed record SourceFile(string Path, long LengthBytes)
    {
        public override string ToString() => Path;
    }

    /// <summary>
    /// 枚举一个目录（含子目录）里指定模式的文件，按修改时间倒序取最近 maxFiles 个
    /// （沿用 magpie"每店最多 200 个最近文件"的预算）。
    /// </summary>
    protected static IReadOnlyList<SourceFile> FindFiles(string directory, string searchPattern, int maxFiles = 200)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(maxFiles)
                .Select(f => new SourceFile(f.FullName, f.Length))
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>JSONL 逐行读：空行/坏行跳过（客户端正在写会产生半行）。超大文件只读尾部。静态以便宽容解析器复用。</summary>
    internal static IEnumerable<string> ReadLinesFor(string path, long maxBytes = 32 * 1024 * 1024)
    {
        // 先把内容读进内存再迭代：C# 不允许 yield return 出现在 try-catch 里，
        // 而文件随时可能被客户端进程写锁
        var lines = new List<string>();

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                return lines;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > maxBytes)
            {
                stream.Seek(-maxBytes, SeekOrigin.End);
                // 丢掉掐断的半行
                using var align = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1, leaveOpen: true);
                _ = align.ReadLine();
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 2)
                {
                    lines.Add(line);
                }
            }
        }
        catch (IOException)
        {
        }

        return lines;
    }

    /// <summary>整个文件读成字符串（给非 JSONL 的 JSON 用，如 Gemini 的会话文件）。</summary>
    protected static string? ReadAllSafe(string path, long maxBytes = 32 * 1024 * 1024)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > maxBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// JSONL 会话文件的宽容解析：任何一层出现 <c>usage</c> 对象就取，
/// 字段同时兼容 snake_case / camelCase。Kimi Code 与 Pi 都用它
/// （两家格式随版本变，先按 magpie 文档的路径探测，宽容兜底）。
/// </summary>
internal sealed class GenericJsonlUsageReader
{
    private readonly string _providerId;
    private readonly string _fallbackModel;

    public GenericJsonlUsageReader(string providerId, string fallbackModel)
    {
        _providerId = providerId;
        _fallbackModel = fallbackModel;
    }

    public IEnumerable<AgentUsageEntry> Read(string path)
    {
        foreach (var line in FileAgentTokenSource.ReadLinesFor(path))
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var usage = FindUsage(doc.RootElement);
                if (usage is null)
                {
                    continue;
                }

                var input = Get(usage.Value, "input_tokens", "inputTokens");
                var output = Get(usage.Value, "output_tokens", "outputTokens");
                var cached = Get(usage.Value, "cached_input_tokens", "cachedTokens");
                var reasoning = Get(usage.Value, "reasoning_output_tokens", "reasoningTokens",
                    "reasoning_tokens", "thinkingTokens");
                if (input + output + cached + reasoning == 0)
                {
                    continue;
                }

                var timestamp = CodexAgentSource.TryGetString(doc.RootElement, "timestamp") is { } ts &&
                                DateTimeOffset.TryParse(ts, out var t)
                    ? t.ToLocalTime()
                    : DateTimeOffset.Now;

                var model = CodexAgentSource.TryGetString(doc.RootElement, "model") ?? _fallbackModel;

                yield return new AgentUsageEntry(
                    timestamp,
                    model,
                    _providerId,
                    new TokenUsage(input - cached, output, reasoning, cached, 0));
            }
        }
    }

    private static JsonElement? FindUsage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("usage") && property.Value.ValueKind == JsonValueKind.Object)
            {
                return property.Value;
            }

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var found = FindUsage(property.Value);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static long Get(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = CodexAgentSource.GetLong(element, name);
            if (value != 0)
            {
                return value;
            }
        }

        return 0;
    }
}
