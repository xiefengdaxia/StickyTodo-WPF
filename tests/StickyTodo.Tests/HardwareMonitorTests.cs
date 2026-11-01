using StickyTodo.Core.Hardware;

namespace StickyTodo.Tests;

public sealed class HardwareMonitorTests
{
    [Fact]
    public void 构造不抛异常_读数在合理范围()
    {
        // 计数器被禁的机器上 IsAvailable=false，但整个流程不能炸
        using var monitor = new HardwareMonitor();
        var sample = monitor.Read();

        Assert.InRange(sample.CpuPercent, 0, 100);
        Assert.InRange(sample.MemoryPercent, 0, 100);
        Assert.True(sample.MemoryUsedBytes <= sample.MemoryTotalBytes);
        Assert.True(sample.DiskReadBytesPerSecond >= 0);
        Assert.True(sample.DiskWriteBytesPerSecond >= 0);
        Assert.InRange(sample.DiskActivePercent, 0, 100);
        Assert.True(sample.NetworkReceivedBytesPerSecond >= 0);
        Assert.True(sample.NetworkSentBytesPerSecond >= 0);

        if (sample.GpuPercent.HasValue)
        {
            Assert.InRange(sample.GpuPercent.Value, 0, 100);
        }
    }

    [Fact]
    public void 内存文本_格式为已用斜总量()
    {
        using var monitor = new HardwareMonitor();
        var sample = monitor.Read();

        if (monitor.IsAvailable)
        {
            Assert.Matches(@"^\d+\.\d / \d+\.\d GB$", sample.MemoryText);
        }
    }
}
