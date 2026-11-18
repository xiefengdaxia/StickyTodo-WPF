using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using StickyTodo.Infrastructure.Token.Agents;

namespace StickyTodo.Tests;

/// <summary>
/// 缓存并发回归：同一个源会被两路同时读（看板定时刷新 + 采集脚本的 get_token_stats）。
/// 以前 <see cref="FileAgentTokenSource"/> 的按天/按文件缓存是裸 Dictionary，
/// 并发写会抛 "Operations that change non-concurrent collections must have exclusive access"，
/// 整个 get_token_stats 直接失败（价格计算看板就会报 Unexpected token '错'）。
/// </summary>
public sealed class AgentTokenCacheConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "agent-cache-" + Guid.NewGuid().ToString("N"));

    public AgentTokenCacheConcurrencyTests()
    {
        Directory.CreateDirectory(_dir);

        // 多放几个文件：每个文件都要解析一次，缓存写的窗口足够大
        for (var i = 0; i < 8; i++)
        {
            var lines = string.Join('\n', Enumerable.Repeat("{}", 40));
            File.WriteAllText(Path.Combine(_dir, $"session-{i}.jsonl"), lines);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 并发读取同一个源不会把缓存搞坏()
    {
        var source = new FakeFileSource(_dir);
        var fromDay = DateOnly.FromDateTime(DateTime.Now).AddDays(-2);
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var counts = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 96, _ =>
        {
            try
            {
                counts.Add(source.ReadEntries(fromDay).Count());
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        });

        Assert.Empty(errors);

        // 缓存被并发写坏时除了抛异常，还可能少算/多算；并行跑完结果必须一致
        Assert.All(counts, count => Assert.Equal(counts.First(), count));
        Assert.Equal(8 * 40, counts.First());
    }

    /// <summary>最小的文件源：每个文件产固定条数的记录（故意慢一点，拉大并发窗口）。</summary>
    private sealed class FakeFileSource(string directory) : FileAgentTokenSource
    {
        protected override string AgentIdCore => "fake";
        protected override string DisplayNameCore => "Fake";
        protected override string SourceDescriptionCore => "fake · " + directory;

        protected override IReadOnlyList<SourceFile> EnumerateFiles() =>
            Directory.GetFiles(directory, "*.jsonl")
                .Select(path => new SourceFile(path, new FileInfo(path).Length))
                .ToList();

        protected override IEnumerable<AgentUsageEntry> ParseFile(SourceFile file)
        {
            Thread.Sleep(1);

            foreach (var _ in File.ReadAllLines(file.Path))
            {
                yield return new AgentUsageEntry(
                    DateTimeOffset.Now,
                    "fake-model",
                    "fake",
                    new TokenUsage(10, 5, 0, 0, 0));
            }
        }
    }
}
