using System.Text;
using System.Text.Json;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using ZstdSharp;

namespace StickyTodo.Infrastructure.Token.Agents;

/// <summary>
/// DSH（DeepSeek Harness 桌面端）的本地账本：
/// <c>~/.dsh/sessions/&lt;workspace&gt;/session-&lt;id&gt;/session.v4.jsonl.zstd</c>。
///
/// <para>文件是 zstd 压缩的 JSONL，一行一个事件。token 用量只出现在
/// <c>type = assistant/message</c> 的事件里：</para>
/// <code>
/// {"type":"assistant/message","time":1795147999838,
///  "data":{"usage":{"inputTokens":113,"outputTokens":158,
///                   "totalTokens":29967,"cacheReadTokens":29696},
///          "message":{"source":{"provider":"magpie","model":"…"}}}}
/// </code>
///
/// <para><b>口径</b>：<c>inputTokens</c> 不含缓存读（total = input + output + cacheRead），
/// 所以直接按「新鲜输入 / 输出 / 缓存读」三段进 <see cref="TokenUsage"/>；
/// 没有缓存写字段，按 0 计。</para>
/// </summary>
internal sealed class DshAgentSource : FileAgentTokenSource
{
    private const string DefaultSessionsRelativePath = ".dsh/sessions";

    private readonly string _sessionsDirectory;

    public DshAgentSource(string? sessionsDirectory = null)
    {
        _sessionsDirectory = sessionsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DefaultSessionsRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    protected override string AgentIdCore => "dsh";
    protected override string DisplayNameCore => "DSH";
    protected override string SourceDescriptionCore => $"DSH 会话 · {_sessionsDirectory}";

    protected override IReadOnlyList<SourceFile> EnumerateFiles()
        => FindFiles(_sessionsDirectory, "session*.jsonl.zstd", maxFiles: 300);

    protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
    {
        var entries = new List<AgentUsageEntry>();

        try
        {
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var decompressed = new DecompressionStream(stream);
            using var reader = new StreamReader(decompressed, Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                // 事件里绝大多数是工具调用/权限，只挑带 usage 的 assistant 消息
                if (line.Length < 2 || line.IndexOf("\"assistant/message\"", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var entry = TryParse(line);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (Exception)
        {
            // 客户端正在写的会话可能只写了一半（zstd 帧不完整）：整份跳过，
            // 下轮 mtime 变了会重读。
        }

        return entries;
    }

    private static AgentUsageEntry? TryParse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant/message")
            {
                return null;
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("usage", out var usage))
            {
                return null;
            }

            var model = "DSH";
            var provider = "dsh";
            if (data.TryGetProperty("message", out var message) &&
                message.TryGetProperty("source", out var source))
            {
                if (source.TryGetProperty("model", out var modelValue) &&
                    modelValue.GetString() is { Length: > 0 } modelName)
                {
                    model = modelName;
                }

                if (source.TryGetProperty("provider", out var providerValue) &&
                    providerValue.GetString() is { Length: > 0 } providerName)
                {
                    provider = providerName;
                }
            }

            var timestamp = root.TryGetProperty("time", out var time) && time.TryGetInt64(out var epochMs)
                ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime()
                : DateTimeOffset.Now;

            var tokenUsage = new TokenUsage(
                GetLong(usage, "inputTokens"),
                GetLong(usage, "outputTokens"),
                0,
                GetLong(usage, "cacheReadTokens"),
                GetLong(usage, "cacheWriteTokens"));

            return tokenUsage.Total == 0
                ? null
                : new AgentUsageEntry(timestamp, model, provider, tokenUsage);
        }
        catch (JsonException)
        {
            // 半行/坏行跳过
            return null;
        }
    }

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
