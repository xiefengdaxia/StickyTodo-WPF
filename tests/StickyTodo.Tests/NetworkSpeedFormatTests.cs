using StickyTodo.Core.Net;

namespace StickyTodo.Tests;

public sealed class NetworkSpeedFormatTests
{
    [Theory]
    [InlineData(0, "0 KB/s")]
    [InlineData(1, "0 KB/s")]                 // 不足 1 KB（整数档抹平）
    [InlineData(1023, "1 KB/s")]
    [InlineData(512 * 1024, "512 KB/s")]
    [InlineData(1024 * 1024, "1.0 MB/s")]
    [InlineData(12.5 * 1024 * 1024, "12.5 MB/s")]
    [InlineData(2.5 * 1024 * 1024 * 1024, "2.5 GB/s")]
    public void 单位自动换档_整数(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, NetworkSpeedMonitor.Format(bytesPerSecond));
    }

    [Theory]
    [InlineData(0, "0.0 KB/s")]
    [InlineData(51, "0.0 KB/s")]              // 不足 0.05 KB
    [InlineData(51.2, "0.1 KB/s")]            // 0.1 KB 精度
    [InlineData(512 * 1024, "512.0 KB/s")]
    [InlineData(1024 * 1024, "1.0 MB/s")]
    [InlineData(12.5 * 1024 * 1024, "12.5 MB/s")]
    [InlineData(2.5 * 1024 * 1024 * 1024, "2.5 GB/s")]
    public void 单位自动换档_精确小数(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, NetworkSpeedMonitor.Format(bytesPerSecond, precise: true));
    }

    [Theory]
    [InlineData(-1, "0 KB/s")]
    [InlineData(double.NaN, "0 KB/s")]
    public void 异常输入不产生奇怪文本(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, NetworkSpeedMonitor.Format(bytesPerSecond));
        Assert.Equal("0.0 KB/s", NetworkSpeedMonitor.Format(bytesPerSecond, precise: true));
    }

    [Fact]
    public void 构造不抛异常_不可用时读数为零()
    {
        // 在 CI/计数器被禁的机器上 IsAvailable 可能是 false，但整个流程不能炸
        using var monitor = new NetworkSpeedMonitor();
        var (received, sent) = monitor.ReadBytesPerSecond();

        if (!monitor.IsAvailable)
        {
            Assert.Equal(0, received);
            Assert.Equal(0, sent);
        }
    }
}
