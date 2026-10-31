using StickyTodo.Core.Entities;
using StickyTodo.Core.Net;

namespace StickyTodo.Tests;

public sealed class ProcessNetworkMonitorTests
{
    [Fact]
    public void 构造不抛异常_读数返回有序列表()
    {
        // 计数器被禁/无网卡的机器上 IsAvailable=false，但不许炸
        using var monitor = new ProcessNetworkMonitor();
        var (down, up) = monitor.Read();

        Assert.NotNull(down);
        Assert.NotNull(up);
        Assert.All(down, s => Assert.True(s.Received >= 0));
        Assert.All(up, s => Assert.True(s.Sent >= 0));
    }

    [Fact]
    public void 读数按速率降序()
    {
        using var monitor = new ProcessNetworkMonitor();
        var (down, up) = monitor.Read();

        for (var i = 1; i < down.Count; i++)
        {
            Assert.True(down[i - 1].Received >= down[i].Received, "下载组未按速率降序");
        }

        for (var i = 1; i < up.Count; i++)
        {
            Assert.True(up[i - 1].Sent >= up[i].Sent, "上传组未按速率降序");
        }
    }

    [Fact]
    public void 行数据字段完整_进程名带pid()
    {
        using var monitor = new ProcessNetworkMonitor();
        var (down, up) = monitor.Read();
        var any = down.Concat(up).FirstOrDefault();

        if (any.ProcessId != 0)
        {
            Assert.False(string.IsNullOrWhiteSpace(any.ProcessName));
            Assert.Matches(@"^.+ \(\d+\)$", any.ProcessName);
        }
    }
}
