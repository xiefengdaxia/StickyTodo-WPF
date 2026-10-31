using System.Diagnostics;
using System.Runtime.Versioning;

namespace StickyTodo.Core.Net;

/// <summary>
/// 网速采样：读 Windows 性能计数器拿「所有网卡合计」的每秒下载/上传字节数。
///
/// 为什么不用 "_Total" 实例：不少机器（尤其装了 Hyper-V / 改过注册表的）根本没有
/// "_Total"，只有各网卡实例，所以这里自己枚举实例、逐个读再求和。
/// 为什么不用 GetIfTable2：那个 API 对断开的虚拟网卡会报 UNKNOWN 类型还要自己算差分，
/// 性能计数器是系统算好的速率，Win7/Win11 行为一致。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkSpeedMonitor : IDisposable
{
    private const string CategoryName = "Network Interface";
    private const string ReceivedName = "Bytes Received/sec";
    private const string SentName = "Bytes Sent/sec";

    private List<PerformanceCounter>? _received;
    private List<PerformanceCounter>? _sent;
    private bool _available;

    public NetworkSpeedMonitor()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(CategoryName))
            {
                return;
            }

            var instances = new PerformanceCounterCategory(CategoryName).GetInstanceNames();
            _received = [];
            _sent = [];

            foreach (var instance in instances)
            {
                _received.Add(NewCounter(ReceivedName, instance));
                _sent.Add(NewCounter(SentName, instance));
            }

            if (_received.Count == 0)
            {
                DisposeCounters();
                return;
            }

            // 第一次 NextValue() 拿到的是 0（没有上一个采样点），先扔掉
            Prime(_received);
            Prime(_sent);

            _available = true;
        }
        catch (Exception)
        {
            // 计数器被组策略关掉 / 权限不足：界面显示「—」就好，别抛
            DisposeCounters();
        }
    }

    /// <summary>计数器是否可用（不可用时界面隐藏网速行）。</summary>
    public bool IsAvailable => _available;

    /// <summary>当前每秒下载/上传的字节数（两次调用之间的平均速率，所有网卡合计）。</summary>
    public (double Received, double Sent) ReadBytesPerSecond()
    {
        if (!_available || _received is null || _sent is null)
        {
            return (0, 0);
        }

        try
        {
            return (Sum(_received), Sum(_sent));
        }
        catch (InvalidOperationException)
        {
            // 某块网卡中途消失（拔 USB 网卡/断 VPN）：本轮按 0 处理，下次采样自愈
            return (0, 0);
        }
    }

    /// <summary>把字节速率格式化成人类可读文本，单位自动换档（KB/s / MB/s / GB/s）。</summary>
    public static string Format(double bytesPerSecond)
    {
        if (bytesPerSecond < 0 || double.IsNaN(bytesPerSecond))
        {
            return "0 KB/s";
        }

        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;

        return bytesPerSecond switch
        {
            // 1 MB/s 以下固定 KB/s（保留 0 位），往上逐级换档
            >= gb => $"{bytesPerSecond / gb:0.0} GB/s",
            >= mb => $"{bytesPerSecond / mb:0.0} MB/s",
            _ => $"{Math.Max(0, bytesPerSecond / kb):0} KB/s",
        };
    }

    public void Dispose() => DisposeCounters();

    private static PerformanceCounter NewCounter(string name, string instance) =>
        new(CategoryName, name, instance, readOnly: true);

    private static void Prime(List<PerformanceCounter> counters)
    {
        foreach (var counter in counters)
        {
            _ = counter.NextValue();
        }
    }

    private static double Sum(List<PerformanceCounter> counters)
    {
        double total = 0;
        foreach (var counter in counters)
        {
            total += counter.NextValue();
        }

        return total;
    }

    private void DisposeCounters()
    {
        if (_received is not null)
        {
            foreach (var counter in _received)
            {
                counter.Dispose();
            }
        }

        if (_sent is not null)
        {
            foreach (var counter in _sent)
            {
                counter.Dispose();
            }
        }

        _received = null;
        _sent = null;
        _available = false;
    }
}
