using System.Text.Json;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.Infrastructure.Token.Agents;

/// <summary>
/// ZCode：~/.zcode/cli/db/db.sqlite → model_usage 表。
/// 每行是一次真实模型请求（attempt_index &gt; 0 是重试，也计入真实计费口径）。
/// 时间戳毫秒。缓存字段：cache_read_input_tokens（读）/ cache_creation_input_tokens（写）。
/// </summary>
internal sealed class ZcodeAgentSource : FileAgentTokenSource
{
    private const string DefaultRelativePath = ".zcode/cli/db/db.sqlite";

    private readonly string _databasePath;

    public ZcodeAgentSource(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    protected override string AgentIdCore => "zcode";
    protected override string DisplayNameCore => "ZCode";
    protected override string SourceDescriptionCore => $"ZCode · {_databasePath}";

    private IReadOnlyList<SourceFile>? _files;

    protected override IReadOnlyList<SourceFile> EnumerateFiles()
        => File.Exists(_databasePath)
            ? _files ??= [new SourceFile(_databasePath, 0)]
            : [];

    protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = file.Path,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
                Pooling = false, // 客户端正在写这个库，只读连接不抢写锁
            }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT started_at,
                   coalesce(model_id, ''),
                   coalesce(provider_id, ''),
                   coalesce(input_tokens, 0),
                   coalesce(output_tokens, 0),
                   coalesce(reasoning_tokens, 0),
                   coalesce(cache_read_input_tokens, 0),
                   coalesce(cache_creation_input_tokens, 0)
              FROM model_usage
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var input = reader.GetInt64(3);
            var output = reader.GetInt64(4);
            var reasoning = reader.GetInt64(5);
            var cacheRead = reader.GetInt64(6);
            var cacheWrite = reader.GetInt64(7);

            // 鉴权失败 / 全零行不是一次有效调用
            if (input + output + reasoning + cacheRead + cacheWrite == 0)
            {
                continue;
            }

            yield return new AgentUsageEntry(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).ToLocalTime(),
                reader.GetString(1),
                reader.GetString(2),
                new TokenUsage(input, output, reasoning, cacheRead, cacheWrite));
        }
    }
}

/// <summary>
/// Codex：~/.codex/sessions/**/rollout-*.jsonl。
/// 关键口径：`token_count` 事件里 `total_token_usage` 是会话累计值，逐行求和会爆炸式重复计；
/// `last_token_usage` 才是本次调用的增量——只累加它。
/// </summary>
internal sealed class CodexAgentSource : FileAgentTokenSource
{
    private const string RootRelativePath = ".codex/sessions";

    private readonly string _root;

    public CodexAgentSource(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RootRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    protected override string AgentIdCore => "codex";
    protected override string DisplayNameCore => "Codex";
    protected override string SourceDescriptionCore => $"Codex · {_root}";

    protected override IReadOnlyList<SourceFile> EnumerateFiles()
        => FindFiles(_root, "rollout-*.jsonl");

    protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
    {
        string? model = null;

        foreach (var line in ReadLinesFor(file.Path))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue; // 半行 / 坏行
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl))
                {
                    continue;
                }

                var type = typeEl.GetString();

                // 会话元数据里的模型
                if (type == "session_meta" &&
                    root.TryGetProperty("payload", out var meta) &&
                    meta.ValueKind == JsonValueKind.Object)
                {
                    model = TryGetString(meta, "model") ?? model;
                    continue;
                }

                if (type != "event_msg" ||
                    !root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object ||
                    !TryGetString(payload, "type")!.Equals("token_count", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                {
                    continue; // info=null 是轮次结束后的空事件
                }

                if (!info.TryGetProperty("last_token_usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var input = GetLong(usage, "input_tokens");
                var cached = GetLong(usage, "cached_input_tokens");
                var output = GetLong(usage, "output_tokens");
                var reasoning = GetLong(usage, "reasoning_output_tokens");
                if (input + cached + output + reasoning == 0)
                {
                    continue;
                }

                // codex 的 cached_input_tokens 是 input 的一部分（非独立桶）
                var timestamp = TryGetString(root, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var t)
                    ? t.ToLocalTime()
                    : DateTimeOffset.Now;

                yield return new AgentUsageEntry(
                    timestamp,
                    model ?? "codex",
                    "openai",
                    new TokenUsage(
                        input - cached, // 去掉缓存后的真实输入
                        output,
                        reasoning,
                        cached,
                        0));
            }
        }
    }

    internal static string? TryGetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static long GetLong(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var result)
            ? result
            : 0;
}

/// <summary>
/// WorkBuddy：~/.workbuddy/projects/*/&lt;session&gt;.jsonl。
/// 关键口径：只统计 <c>type=="function_call"</c> 行上的 <c>usage</c>（每次模型调用的单次值）；
/// <c>type=="message"</c> 行的 usage 是该轮的累计值，求和会重复计。
/// </summary>
internal sealed class WorkbuddyAgentSource : FileAgentTokenSource
{
    private const string RootRelativePath = ".workbuddy/projects";

    private readonly string _root;

    public WorkbuddyAgentSource(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RootRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    protected override string AgentIdCore => "workbuddy";
    protected override string DisplayNameCore => "WorkBuddy";
    protected override string SourceDescriptionCore => $"WorkBuddy · {_root}";

    protected override IReadOnlyList<SourceFile> EnumerateFiles()
        => FindFiles(_root, "*.jsonl");

    protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
    {
        foreach (var line in ReadLinesFor(file.Path))
        {
            if (!line.Contains("\"type\":\"function_call\"", StringComparison.Ordinal) ||
                !line.Contains("\"usage\":{\"requests\"", StringComparison.Ordinal))
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
                var root = doc.RootElement;
                var timestamp = root.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()
                    : DateTimeOffset.Now;

                if (!root.TryGetProperty("providerData", out var providerData) ||
                    providerData.ValueKind != JsonValueKind.Object ||
                    !providerData.TryGetProperty("usage", out var usage) ||
                    usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var input = CodexAgentSource.GetLong(usage, "inputTokens");
                var output = CodexAgentSource.GetLong(usage, "outputTokens");
                if (input + output == 0)
                {
                    continue;
                }

                var cached = 0L;
                if (usage.TryGetProperty("inputTokensDetails", out var details) &&
                    details.ValueKind == JsonValueKind.Array &&
                    details.GetArrayLength() > 0 &&
                    details[0].TryGetProperty("cached_tokens", out var cachedEl))
                {
                    cached = cachedEl.TryGetInt64(out var c) ? c : 0;
                }

                var reasoning = 0L;
                if (usage.TryGetProperty("outputTokensDetails", out var outDetails) &&
                    outDetails.ValueKind == JsonValueKind.Array &&
                    outDetails.GetArrayLength() > 0 &&
                    outDetails[0].TryGetProperty("reasoning_tokens", out var reasoningEl))
                {
                    reasoning = reasoningEl.TryGetInt64(out var r) ? r : 0;
                }

                var model = CodexAgentSource.TryGetString(providerData, "model") ?? "workbuddy";

                yield return new AgentUsageEntry(
                    timestamp,
                    model,
                    "workbuddy",
                    new TokenUsage(input - cached, output, reasoning, cached, 0));
            }
        }
    }
}

/// <summary>
/// Claude Code：~/.claude/projects/*/*.jsonl（magpie 同款路径）。
/// assistant 行的 message.usage 是该次响应的用量；cache_creation→CacheWrite。
/// </summary>
internal sealed class ClaudeAgentSource : FileAgentTokenSource
{
    private const string RootRelativePath = ".claude/projects";

    private readonly string _root;

    public ClaudeAgentSource(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RootRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    protected override string AgentIdCore => "claude";
    protected override string DisplayNameCore => "Claude Code";
    protected override string SourceDescriptionCore => $"Claude Code · {_root}";

    protected override IReadOnlyList<SourceFile> EnumerateFiles()
        => FindFiles(_root, "*.jsonl");

    protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
    {
        foreach (var line in ReadLinesFor(file.Path))
        {
            if (!line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) ||
                !line.Contains("\"usage\"", StringComparison.Ordinal))
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
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var input = CodexAgentSource.GetLong(usage, "input_tokens");
                var output = CodexAgentSource.GetLong(usage, "output_tokens");
                var cacheRead = CodexAgentSource.GetLong(usage, "cache_read_input_tokens");
                var cacheWrite = CodexAgentSource.GetLong(usage, "cache_creation_input_tokens");
                var reasoning = 0L;
                if (usage.TryGetProperty("output_tokens_details", out var details) &&
                    details.ValueKind == JsonValueKind.Object)
                {
                    reasoning = CodexAgentSource.GetLong(details, "reasoning_tokens");
                }

                if (input + output + cacheRead + cacheWrite == 0)
                {
                    continue;
                }

                var timestamp = CodexAgentSource.TryGetString(root, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var t)
                    ? t.ToLocalTime()
                    : DateTimeOffset.Now;

                var model = CodexAgentSource.TryGetString(message, "model") ?? "claude";

                yield return new AgentUsageEntry(
                    timestamp,
                    model,
                    "anthropic",
                    new TokenUsage(input, output, reasoning, cacheRead, cacheWrite));
            }
        }
    }
}

/// <summary>
/// Gemini CLI：~/.gemini/tmp/*/chats/session-*.json（magpie 同款路径）。
/// messages[].tokens 有 input/output/cached/thoughts；cached→CacheRead、thoughts→Reasoning。
/// </summary>
internal sealed class GeminiAgentSource : FileAgentTokenSource
{
    private const string RootRelativePath = ".gemini/tmp";

    private readonly string _root;

    public GeminiAgentSource(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RootRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    protected override string AgentIdCore => "gemini";
    protected override string DisplayNameCore => "Gemini CLI";
    protected override string SourceDescriptionCore => $"Gemini CLI · {_root}";

    protected override IReadOnlyList<SourceFile> EnumerateFiles()
        => FindFiles(_root, "session-*.json");

    protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
    {
        var json = ReadAllSafe(file.Path);
        if (string.IsNullOrEmpty(json))
        {
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("tokens", out var tokens) ||
                    tokens.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var input = CodexAgentSource.GetLong(tokens, "input");
                var output = CodexAgentSource.GetLong(tokens, "output");
                var cached = CodexAgentSource.GetLong(tokens, "cached");
                var reasoning = CodexAgentSource.GetLong(tokens, "thoughts");
                if (input + output + cached + reasoning == 0)
                {
                    continue;
                }

                var timestamp = CodexAgentSource.TryGetString(message, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var t)
                    ? t.ToLocalTime()
                    : File.GetLastWriteTime(File.Exists(file.Path) ? file.Path : ".");

                var model = CodexAgentSource.TryGetString(message, "model") ?? "gemini";

                yield return new AgentUsageEntry(
                    timestamp,
                    model,
                    "google",
                    new TokenUsage(input - cached, output, reasoning, cached, 0));
            }
        }
    }
}

/// <summary>
/// Qoder CN：~/.qoder-cn/logs/sessions/**/*.jsonl。
/// model.response.completed 行带 tokens，但官方可能不下发（实测全 0）——如实统计，不估算。
/// </summary>
internal sealed class QoderAgentSource : FileAgentTokenSource
{
    private const string RootRelativePath = ".qoder-cn/logs/sessions";

    private readonly string _root;

    public QoderAgentSource(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RootRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    protected override string AgentIdCore => "qoder";
    protected override string DisplayNameCore => "Qoder";
    protected override string SourceDescriptionCore => $"Qoder · {_root}";

    protected override IReadOnlyList<SourceFile> EnumerateFiles()
        => FindFiles(_root, "*.jsonl");

    protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
    {
        foreach (var line in ReadLinesFor(file.Path))
        {
            if (!line.Contains("\"type\":\"model.response.completed\"", StringComparison.Ordinal))
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
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var input = CodexAgentSource.GetLong(data, "input_tokens");
                var output = CodexAgentSource.GetLong(data, "output_tokens");
                var cacheRead = CodexAgentSource.GetLong(data, "cache_read_input_tokens");
                var cacheWrite = CodexAgentSource.GetLong(data, "cache_creation_input_tokens");
                if (input + output + cacheRead + cacheWrite == 0)
                {
                    continue; // 官方未下发用量
                }

                var timestamp = CodexAgentSource.TryGetString(root, "ts") is { } ts && DateTimeOffset.TryParse(ts, out var t)
                    ? t.ToLocalTime()
                    : DateTimeOffset.Now;

                var model = CodexAgentSource.TryGetString(data, "model") ?? "qoder";

                yield return new AgentUsageEntry(
                    timestamp,
                    model,
                    "qoder",
                    new TokenUsage(input, output, 0, cacheRead, cacheWrite));
            }
        }
    }
}

/// <summary>
/// Kimi Code：~/.kimi/。本机未发现会话账本；格式随版本变，
/// 按"任意层出现 usage 块"宽容解析（字段兼容 snake/camel）。
/// </summary>
internal sealed class KimiAgentSource : FileAgentTokenSource
{
    private const string RootRelativePath = ".kimi";

    private readonly GenericJsonlUsageReader _reader;

    public KimiAgentSource(string? root = null)
    {
        var full = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RootRelativePath.Replace('/', Path.DirectorySeparatorChar));
        _reader = new GenericJsonlUsageReader("kimi", "kimi");
        _rootPath = full;
    }

    private readonly string _rootPath;

    protected override string AgentIdCore => "kimi";
    protected override string DisplayNameCore => "Kimi Code";
    protected override string SourceDescriptionCore => $"Kimi Code · {_rootPath}";

    protected override IReadOnlyList<SourceFile> EnumerateFiles()
        => FindFiles(_rootPath, "*.jsonl");

    protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
        => _reader.Read(file.Path);
}

/// <summary>
/// Pi：~/.pi/agent/sessions/**。v3 JSONL；字段兼容 snake/camel（与 magpie 同路径）。
/// </summary>
internal sealed class PiAgentSource : FileAgentTokenSource
{
    private const string RootRelativePath = ".pi/agent/sessions";

    private readonly string _rootPath;
    private readonly GenericJsonlUsageReader _reader;

    public PiAgentSource(string? root = null)
    {
        _rootPath = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RootRelativePath.Replace('/', Path.DirectorySeparatorChar));
        _reader = new GenericJsonlUsageReader("pi", "pi");
    }

    protected override string AgentIdCore => "pi";
    protected override string DisplayNameCore => "Pi";
    protected override string SourceDescriptionCore => $"Pi · {_rootPath}";

    protected override IReadOnlyList<SourceFile> EnumerateFiles()
        => FindFiles(_rootPath, "*.jsonl");

    protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
        => _reader.Read(file.Path);
}
