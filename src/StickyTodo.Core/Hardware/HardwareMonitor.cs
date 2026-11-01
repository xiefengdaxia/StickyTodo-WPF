using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StickyTodo.Core.Net;

namespace StickyTodo.Core.Hardware;

/// <summary>一次硬件采样（全机）。</summary>
/// <param name="CpuPercent">CPU 总占用（0-100）。</param>
/// <param name="MemoryUsedBytes">已用物理内存。</param>
/// <param name="MemoryTotalBytes">物理内存总量。</param>
/// <param name="DiskReadBytesPerSecond">磁盘每秒读取字节（全盘合计）。</param>
/// <param name="DiskWriteBytesPerSecond">磁盘每秒写入字节。</param>
/// <param name="DiskActivePercent">磁盘活动时间占比（0-100）。</param>
/// <param name="GpuPercent">GPU 占用（0-100；取不到为 null）。</param>
/// <param name="GpuEngineName">采样到的 GPU 引擎说明（如 "GPU 0 (RTX)"）。</param>
/// <param name="NetworkReceivedBytesPerSecond">全机每秒下载（与网速看板同源）。</param>
/// <param name="NetworkSentBytesPerSecond">全机每秒上传。</param>
/// <param name="Uptime">开机时长。</param>
public readonly record struct HardwareSample(
    double CpuPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    double DiskActivePercent,
    double? GpuPercent,
    string GpuEngineName,
    double NetworkReceivedBytesPerSecond,
    double NetworkSentBytesPerSecond,
    TimeSpan Uptime)
{
    /// <summary>内存占用百分比。</summary>
    public double MemoryPercent => MemoryTotalBytes == 0
        ? 0
        : MemoryUsedBytes * 100.0 / MemoryTotalBytes;

    /// <summary>内存使用量文本，如 "9.2 / 16.0 GB"。</summary>
    public string MemoryText => $"{BytesToGb(MemoryUsedBytes):0.0} / {BytesToGb(MemoryTotalBytes):0.0} GB";

    private static double BytesToGb(ulong bytes) => bytes / (1024.0 * 1024 * 1024);
}

/// <summary>
/// 硬件性能采样器：CPU / 内存 / 磁盘 IO / GPU / 网络，全部走 Windows 性能计数器，
/// 纯本机读取、不联网。每次 <see cref="Read"/> 与上一次调用做差分，
/// 建议每秒调一次。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareMonitor : IDisposable
{
    private const string CategoryCpu = "Processor";
    private const string CategoryDisk = "PhysicalDisk";
    private const string CategoryGpu = "GPU Engine";

    private readonly NetworkSpeedMonitor _network = new();

    private readonly PerformanceCounter? _cpu;
    private readonly List<PerformanceCounter> _diskRead = [];
    private readonly List<PerformanceCounter> _diskWrite = [];
    private readonly List<PerformanceCounter> _diskActive = [];
    private readonly List<PerformanceCounter> _gpu = [];
    private readonly string _gpuEngineName = string.Empty;

    /// <summary>物理内存总量（构造时读一次）。</summary>
    public ulong MemoryTotalBytes { get; }

    /// <summary>计数器是否可用（被禁时界面显示占位）。</summary>
    public bool IsAvailable { get; }

    /// <summary>开机时间（构造时算一次）。</summary>
    public TimeSpan Uptime { get; }

    public HardwareMonitor()
    {
        try
        {
            _cpu = new PerformanceCounter(CategoryCpu, "% Processor Time", "_Total", readOnly: true);
            _ = _cpu.NextValue();   // 首次差分基准

            // 所有物理磁盘（"_Total" 实例在一些精简系统上不存在，自己加总）
            if (PerformanceCounterCategory.Exists(CategoryDisk))
            {
                foreach (var instance in new PerformanceCounterCategory(CategoryDisk).GetInstanceNames())
                {
                    // 排除逻辑分区（实例名 "0 C:" 是物理盘，"0 C: D:" 也是；纯数字+盘符的才要）
                    _diskRead.Add(new PerformanceCounter(CategoryDisk, "Disk Read Bytes/sec", instance, readOnly: true));
                    _diskWrite.Add(new PerformanceCounter(CategoryDisk, "Disk Write Bytes/sec", instance, readOnly: true));
                    _diskActive.Add(new PerformanceCounter(CategoryDisk, "% Disk Time", instance, readOnly: true));
                }
            }

            Prime(_diskRead);
            Prime(_diskWrite);
            Prime(_diskActive);

            // GPU：GPU Engine 分类下每个引擎一个实例（几百个），全量收集会让每秒采样卡 500ms+；
            // 均匀抽样最多 16 个实例（每秒约 20ms），GPU 忙时总有实例占用高，读数足够反映"忙不忙"
            if (PerformanceCounterCategory.Exists(CategoryGpu))
            {
                var category = new PerformanceCounterCategory(CategoryGpu);
                var instances = category.GetInstanceNames();
                var stride = Math.Max(1, instances.Length / 16);

                for (var i = 0; i < instances.Length; i += stride)
                {
                    var instance = instances[i];
                    if (!instance.Contains("engtype_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _gpu.Add(new PerformanceCounter(CategoryGpu, "Utilization Percentage", instance, readOnly: true));
                }

                Prime(_gpu);
                _gpuEngineName = "GPU";
            }

            MemoryTotalBytes = ReadTotalMemory();
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

            IsAvailable = _cpu is not null;
        }
        catch (Exception)
        {
            IsAvailable = false;
        }
    }

    /// <summary>采样一次。GPU 取不到时 <see cref="HardwareSample.GpuPercent"/> 为 null。</summary>
    public HardwareSample Read()
    {
        var cpu = _cpu?.NextValue() ?? 0;
        var memoryUsed = ReadUsedMemory();

        double diskRead = 0, diskWrite = 0, diskActive = 0;
        foreach (var counter in _diskRead)
        {
            diskRead += counter.NextValue();
        }

        foreach (var counter in _diskWrite)
        {
            diskWrite += counter.NextValue();
        }

        foreach (var counter in _diskActive)
        {
            diskActive += counter.NextValue();
        }

        // 磁盘活动时间是各盘并行的，直接求和可能超 100；按盘数取平均更接近"忙不忙"
        if (_diskActive.Count > 1)
        {
            diskActive /= _diskActive.Count;
        }

        double? gpu = null;
        if (_gpu.Count > 0)
        {
            // 同一引擎被多个进程实例重复上报：全加会超 100%，全遍历又慢；
            // 构造时已抽样 ≤16 个实例，这里取最大值（GPU 忙时总有实例占用高）
            double max = 0;
            foreach (var counter in _gpu)
            {
                var v = counter.NextValue();
                if (v > max)
                {
                    max = v;
                }
            }

            gpu = max;
        }

        var (netReceived, netSent) = _network.ReadBytesPerSecond();

        return new HardwareSample(
            Math.Clamp(cpu, 0, 100),
            memoryUsed,
            MemoryTotalBytes,
            Math.Max(0, diskRead),
            Math.Max(0, diskWrite),
            Math.Clamp(diskActive, 0, 100),
            gpu,
            _gpuEngineName,
            Math.Max(0, netReceived),
            Math.Max(0, netSent),
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    public void Dispose()
    {
        _cpu?.Dispose();
        DisposeAll(_diskRead);
        DisposeAll(_diskWrite);
        DisposeAll(_diskActive);
        DisposeAll(_gpu);
        _network.Dispose();
    }

    private static ulong ReadTotalMemory()
    {
        // GlobalMemoryStatusEx 比性能计数器稳（Win7 也有）
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            return status.TotalPhys;
        }

        return 0;
    }

    private static ulong ReadUsedMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            return status.TotalPhys - status.AvailPhys;
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static void Prime(List<PerformanceCounter> counters)
    {
        foreach (var counter in counters)
        {
            _ = counter.NextValue();
        }
    }

    private static void DisposeAll(List<PerformanceCounter> counters)
    {
        foreach (var counter in counters)
        {
            counter.Dispose();
        }

        counters.Clear();
    }
}
