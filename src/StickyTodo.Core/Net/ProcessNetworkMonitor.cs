using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StickyTodo.Core.Net;

/// <summary>一个进程此刻的网络速率（下载/上传，字节每秒）。</summary>
/// <param name="ProcessId">进程 ID；进程已退出时为 0。</param>
/// <param name="ProcessName">进程名（不含扩展名）。</param>
/// <param name="Received">每秒下载字节。</param>
/// <param name="Sent">每秒上传字节。</param>
public readonly record struct ProcessSpeed(int ProcessId, string ProcessName, double Received, double Sent)
{
    public string Display => NetworkSpeedMonitor.Format(Received);
}

/// <summary>
/// 按进程统计网速：定时抓系统的 TCP/UDP 连接表，把每条连接归到进程，
/// 再把「全机每秒速率」按连接数比例分摊到进程上。
///
/// 实现说明：
///   * 用 GetExtendedTcpTable/UdpTable 拿 (进程, 本地端口) 集合；
///   * 单条连接拿不到精确用量，所以按"进程占的连接数比例"分摊全机速率——
///     对"哪个进程在大量收/发"这个展示目的足够准（下载/上传共用连接池的
///     场景会有少量串扰，界面上注明"按连接估算"）；
///   * 全机速率来自 <see cref="NetworkSpeedMonitor"/>（性能计数器）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessNetworkMonitor : IDisposable
{
    private const int AF_INET = 2;

    // TCP_STATE: 5 = ESTABLISHED
    private const int TcpStateEstablished = 5;

    /// <summary>每组最多显示的进程数（榜单式，不是完整连接表）。</summary>
    public const int TopCount = 5;

    private readonly NetworkSpeedMonitor _total = new();

    /// <summary>进程名缓存：pid → "名字 (pid)"。进程名一辈子不变，读一次就够；
    /// 不缓存的话每秒 60 次 Process.GetProcessById 要 1.3 秒（拖动卡顿的主因）。</summary>
    private readonly Dictionary<int, string> _nameCache = new();
    private (double Received, double Sent) _lastTotals;
    private bool _hasFreshTotals;

    /// <summary>全机计数器是否可用。</summary>
    public bool IsAvailable => _total.IsAvailable;

    public ProcessNetworkMonitor()
    {
        // 预热：先读一次全机计数器，下一轮才有差分
        _total.ReadBytesPerSecond();
    }

    /// <summary>
    /// 采样一次：返回本轮每秒速率，下载与上传两个列表均已按速率降序排好。
    /// </summary>
    public (IReadOnlyList<ProcessSpeed> Download, IReadOnlyList<ProcessSpeed> Upload) Read(bool reuseLastTotals = false)
    {
        if (reuseLastTotals && _hasFreshTotals)
        {
            // 看板每秒先 ReadTotal() 再 Read()：同一秒内计数器不能读两次，
            // 否则差分窗口只有几毫秒，速率会抖到 0
            return BuildLists(_lastTotals.Received, _lastTotals.Sent);
        }

        var (totalReceived, totalSent) = _total.ReadBytesPerSecond();
        _lastTotals = (totalReceived, totalSent);
        _hasFreshTotals = true;

        return BuildLists(totalReceived, totalSent);
    }

    /// <summary>
    /// 全机合计速率（性能计数器原始值）。看板顶部的合计用这个，
    /// 和桌面时钟挂件显示的是同一个数——列表行是分摊+过滤+取整后的近似值，
    /// 拿行求和必然对不上。
    /// </summary>
    public (double Received, double Sent) ReadTotal()
    {
        var totals = _total.ReadBytesPerSecond();
        _lastTotals = totals;
        _hasFreshTotals = true;
        return totals;
    }

    private (IReadOnlyList<ProcessSpeed> Download, IReadOnlyList<ProcessSpeed> Upload) BuildLists(
        double totalReceived,
        double totalSent)
    {
        var ownerByPort = GetPortOwners();
        var connectionCounts = new Dictionary<int, int>();
        foreach (var owner in ownerByPort.Values)
        {
            connectionCounts[owner] = connectionCounts.TryGetValue(owner, out var n) ? n + 1 : 1;
        }

        var download = new Dictionary<int, double>();
        var upload = new Dictionary<int, double>();
        var totalConnections = connectionCounts.Values.Sum();

        foreach (var (processId, count) in connectionCounts)
        {
            download[processId] = totalReceived * count / totalConnections;
            upload[processId] = totalSent * count / totalConnections;
        }

        // 下载组过滤掉 1 KB/s 以下（上传组不过滤：0.1 KB 精度下小流量也有意义），
        // 各组只显示前 5 名——看板是"谁在大量收/发"的榜单，不是完整连接表
        var downloadList = download
            .Select(kv => ToSpeed(kv.Key, kv.Value, upload.TryGetValue(kv.Key, out var u) ? u : 0))
            .Where(s => s.Received >= 1024)
            .OrderByDescending(s => s.Received)
            .Take(TopCount)
            .ToList();

        var uploadList = upload
            .Select(kv => ToSpeed(kv.Key, download.TryGetValue(kv.Key, out var d) ? d : 0, kv.Value))
            .Where(s => s.Sent > 0)
            .OrderByDescending(s => s.Sent)
            .Take(TopCount)
            .ToList();

        return (downloadList, uploadList);
    }

    private ProcessSpeed ToSpeed(int processId, double received, double sent)
    {
        var name = GetProcessName(processId);
        return new ProcessSpeed(processId, name, received, sent);
    }

    private string GetProcessName(int processId)
    {
        if (_nameCache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        string name;
        try
        {
            using var process = Process.GetProcessById(processId);
            // 同名进程很多（svchost × N），带上 pid 才分得清是哪一个
            name = $"{process.ProcessName} ({processId})";
        }
        catch (Exception)
        {
            name = "系统";
        }

        _nameCache[processId] = name;
        return name;
    }

    public void Dispose()
    {
        _total.Dispose();
    }

    // ---------------- Win32：TCP/UDP 连接表 → 进程 ----------------

    /// <summary>本地端口 → 进程 ID（TCP 建立中的连接 + 所有 UDP 绑定）。</summary>
    private static Dictionary<int, int> GetPortOwners()
    {
        var owners = new Dictionary<int, int>();

        CollectTcpOwners(owners);
        CollectUdpOwners(owners);

        return owners;
    }

    private static void CollectTcpOwners(Dictionary<int, int> owners)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TcpTableClassOwnerPidAll, 0);

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, AF_INET, TcpTableClassOwnerPidAll, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer, 0);
            var rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + 4 + (i * rowSize));
                if (row.State != TcpStateEstablished)
                {
                    continue;
                }

                var port = (row.LocalPort >> 8) | ((row.LocalPort & 0xFF) << 8);
                owners[(int)(port & 0xFFFF)] = (int)row.OwningPid;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CollectUdpOwners(Dictionary<int, int> owners)
    {
        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UdpTableClassOwnerPid, 0);

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, true, AF_INET, UdpTableClassOwnerPid, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer, 0);
            var rowSize = Marshal.SizeOf(typeof(MibUdpRowOwnerPid));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(buffer + 4 + (i * rowSize));
                var port = (row.LocalPort >> 8) | ((row.LocalPort & 0xFF) << 8);
                owners[(int)(port & 0xFFFF)] = (int)row.OwningPid;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    private enum TcpTableClass
    {
        BasicListener = 0,
        BasicConnections = 1,
        BasicAll = 2,
        OwnerPidListener = 3,
        OwnerPidConnections = 4,
        OwnerPidAll = 5,
    }

    private const TcpTableClass TcpTableClassOwnerPidAll = TcpTableClass.OwnerPidAll;
    private const UdpTableClass UdpTableClassOwnerPid = UdpTableClass.OwnerPid;

    private enum UdpTableClass
    {
        Basic = 0,
        OwnerPid = 1,
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int tcpTableLength,
        bool order,
        int family,
        TcpTableClass tableClass,
        uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr udpTable,
        ref int udpTableLength,
        bool order,
        int family,
        UdpTableClass tableClass,
        uint reserved = 0);
}