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

    private readonly NetworkSpeedMonitor _total = new();

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
    public (IReadOnlyList<ProcessSpeed> Download, IReadOnlyList<ProcessSpeed> Upload) Read()
    {
        var (totalReceived, totalSent) = _total.ReadBytesPerSecond();

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

        // 只保留有明显速率的进程，避免几十个 0 KB/s 的行淹没有效信息
        var downloadList = download
            .Select(kv => ToSpeed(kv.Key, kv.Value, upload.TryGetValue(kv.Key, out var u) ? u : 0))
            .Where(s => s.Received > 1024)
            .OrderByDescending(s => s.Received)
            .ToList();

        var uploadList = upload
            .Select(kv => ToSpeed(kv.Key, download.TryGetValue(kv.Key, out var d) ? d : 0, kv.Value))
            .Where(s => s.Sent > 1024)
            .OrderByDescending(s => s.Sent)
            .ToList();

        return (downloadList, uploadList);
    }

    private ProcessSpeed ToSpeed(int processId, double received, double sent)
    {
        var name = GetProcessName(processId);
        return new ProcessSpeed(processId, name, received, sent);
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            // 同名进程很多（svchost × N），带上 pid 才分得清是哪一个
            return $"{process.ProcessName} ({processId})";
        }
        catch (Exception)
        {
            return "系统";
        }
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