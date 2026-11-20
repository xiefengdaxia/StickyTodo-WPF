using System.Text;
using StickyTodo.Core.Entities;
using StickyTodo.Infrastructure.Token.Agents;
using ZstdSharp;

namespace StickyTodo.Tests;

/// <summary>
/// DSH（DeepSeek Harness 桌面端）的会话日志口径：
/// <c>~/.dsh/sessions/**/session*.jsonl.zstd</c>，zstd 压缩的 JSONL；
/// token 用量只在 <c>assistant/message</c> 的 <c>data.usage</c> 里，且
/// <c>inputTokens</c> 不含缓存读（total = input + output + cacheRead）。
/// </summary>
public sealed class DshTokenSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dsh-" + Guid.NewGuid().ToString("N"));

    private const long EventTime1 = 1795147992508;
    private const long EventTime2 = 1795147999838;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void WriteSession(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = string.Join('\n', lines) + "\n";
        var bytes = Encoding.UTF8.GetBytes(text);

        using var file = File.Create(path);
        using var compressed = new CompressionStream(file);
        compressed.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void ReadsUsageFromZstdSessions()
    {
        WriteSession(
            Path.Combine(_root, "--workspace--", "session-abc", "session.v4.jsonl.zstd"),
            """
            {"type":"session","version":4,"id":"session-abc"}
            """,
            """
            {"type":"permission/preset","seq":0,"time":1795147992381,"data":{"preset":"workspace-write"}}
            """,
            """
            {"type":"assistant/message","seq":2,"time":1795147992508,"data":{"usage":{"inputTokens":29352,"outputTokens":353,"totalTokens":29705},"message":{"source":{"provider":"magpie","model":"deepseek-v4.1-flash-1017"}}}}
            """,
            """
            {"type":"assistant/message","seq":3,"time":1795147999838,"data":{"usage":{"inputTokens":113,"outputTokens":158,"totalTokens":29967,"cacheReadTokens":29696},"message":{"source":{"provider":"magpie","model":"deepseek-v4.1-flash-1017"}}}}
            """,
            // 没有 usage 的 assistant 消息：不算一次调用
            """
            {"type":"assistant/message","seq":4,"time":1795148000000,"data":{"message":{"role":"assistant","content":[]}}}
            """);

        var source = new DshAgentSource(_root);
        Assert.True(source.IsAvailable);

        var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(EventTime1).ToLocalTime().DateTime);
        var entries = source.ReadEntries(day).ToList();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("deepseek-v4.1-flash-1017", e.ModelId));
        Assert.All(entries, e => Assert.Equal("magpie", e.ProviderId));
        Assert.All(entries, e => Assert.Equal(1, e.Steps));

        var total = entries.Aggregate(TokenUsage.Empty, (acc, e) => TokenUsage.Add(acc, e.Usage));
        Assert.Equal(29465, total.Input);
        Assert.Equal(511, total.Output);
        Assert.Equal(29696, total.CacheRead);
        Assert.Equal(0, total.CacheWrite);

        // 时间戳按事件里的毫秒数还原
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(EventTime2).ToLocalTime().ToUnixTimeSeconds(),
            entries[1].Timestamp.ToUnixTimeSeconds());
    }

    [Fact]
    public void BrokenSessionFile_IsSkippedWithoutThrowing()
    {
        var dir = Path.Combine(_root, "--workspace--", "session-broken");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "session.v4.jsonl.zstd"), [0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x11, 0x22]);

        var source = new DshAgentSource(_root);

        Assert.True(source.IsAvailable);
        Assert.Empty(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now).AddDays(-1)));
    }

    [Fact]
    public void MissingDirectory_IsNotAvailable()
    {
        var source = new DshAgentSource(Path.Combine(_root, "nope"));

        Assert.False(source.IsAvailable);
        Assert.Empty(source.ReadEntries(DateOnly.FromDateTime(DateTime.Now)));
    }
}
