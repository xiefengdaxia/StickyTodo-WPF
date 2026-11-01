using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Hardware;
using StickyTodo.Core.Net;

namespace StickyTodo.App.ViewModels;

/// <summary>性能看板里 CPU 的一个历史点。</summary>
public sealed class CpuHistoryPoint : ObservableObject
{
    private double _value;

    public double Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

/// <summary>
/// PC 性能看板：CPU（60 秒折线）/ 内存（进度条）/ 磁盘 IO / GPU / 网络 / 开机时长，
/// 每秒刷新。数据来自 <see cref="HardwareMonitor"/>（性能计数器，纯本机）。
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    private readonly HardwareMonitor _monitor = new();
    private readonly DispatcherTimer _timer;

    /// <summary>CPU 折线的历史长度。</summary>
    public const int CpuHistoryLength = 60;

    [ObservableProperty]
    private string _cpuText = "0%";

    [ObservableProperty]
    private double _cpuValue;

    [ObservableProperty]
    private string _memoryText = "—";

    [ObservableProperty]
    private double _memoryValue;

    /// <summary>内存条颜色档位：0 正常（强调色），1 偏高（橙），2 告警（红）。</summary>
    [ObservableProperty]
    private int _memoryLevel;

    /// <summary>剩余物理内存（内存卡右下角小字）。</summary>
    [ObservableProperty]
    private string _memoryFreeText = string.Empty;

    [ObservableProperty]
    private string _diskActiveText = "0%";

    [ObservableProperty]
    private double _diskActiveValue;

    [ObservableProperty]
    private string _diskIoText = "读 0 KB/s   写 0 KB/s";

    [ObservableProperty]
    private string _gpuText = string.Empty;

    /// <summary>GPU 占用值（进度条用）。</summary>
    [ObservableProperty]
    private double _gpuValue;

    /// <summary>GPU 引擎说明（性能计数器实例名，目前固定 "GPU"）。</summary>
    [ObservableProperty]
    private string _gpuEngineName = "GPU";

    /// <summary>GPU 块是否显示（计数器取不到就隐藏整块）。</summary>
    [ObservableProperty]
    private bool _hasGpu = true;

    /// <summary>全机下载速率（网络卡，与网速看板同源）。</summary>
    [ObservableProperty]
    private string _networkDownloadText = "0 KB/s";

    /// <summary>全机上传速率。</summary>
    [ObservableProperty]
    private string _networkUploadText = "0 KB/s";

    /// <summary>CPU 逻辑核心数（标题旁小字）。</summary>
    [ObservableProperty]
    private string _cpuCoresText = $"{Environment.ProcessorCount} 核";

    [ObservableProperty]
    private string _uptimeText = string.Empty;

    /// <summary>CPU 历史点（折线图绑定）。</summary>
    public ObservableCollection<CpuHistoryPoint> CpuHistory { get; } = new();

    public PerformanceViewModel()
    {
        // 预填历史，让折线一开始就是平线而不是从右边长出来
        for (var i = 0; i < CpuHistoryLength; i++)
        {
            CpuHistory.Add(new CpuHistoryPoint());
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>计数器是否可用（不可用时各块显示占位）。</summary>
    public bool IsAvailable => _monitor.IsAvailable;

    /// <summary>窗口打开时开始采样。</summary>
    public void Start()
    {
        _ = RefreshAsync();
        _timer.Start();
    }

    /// <summary>窗口关闭时停采样并释放计数器。</summary>
    public void Stop()
    {
        _timer.Stop();
        _monitor.Dispose();
    }

    private bool _sampling;

    /// <summary>
    /// 采样放后台线程：性能计数器一次全量读取约 30-80ms（GPU/网卡计数器单次调用固有开销），
    /// 在 UI 线程上跑会让拖动掉帧。后台算完把结果抛回 UI 线程。
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_sampling)
        {
            return;
        }

        _sampling = true;
        try
        {
            var sample = await Task.Run(() => _monitor.Read()).ConfigureAwait(true);
            Apply(sample);
        }
        catch (Exception)
        {
            // 单轮失败静默跳过，下一秒再试
        }
        finally
        {
            _sampling = false;
        }
    }

    private void Apply(HardwareSample sample)
    {
        CpuValue = sample.CpuPercent;
        CpuText = $"{sample.CpuPercent:0.0}%";
        PushCpuHistory(sample.CpuPercent);

        MemoryValue = sample.MemoryPercent;
        MemoryText = sample.MemoryText;
        MemoryFreeText = $"剩余 {(sample.MemoryTotalBytes - sample.MemoryUsedBytes) / (1024.0 * 1024 * 1024):0.0} GB";
        // 75% 换橙、90% 换红，和任务管理器的观感一致
        MemoryLevel = sample.MemoryPercent >= 90 ? 2 : sample.MemoryPercent >= 75 ? 1 : 0;

        DiskActiveValue = sample.DiskActivePercent;
        DiskActiveText = $"{sample.DiskActivePercent:0.0}%";
        DiskIoText = $"读 {NetworkSpeedMonitor.Format(sample.DiskReadBytesPerSecond)}   写 {NetworkSpeedMonitor.Format(sample.DiskWriteBytesPerSecond)}";

        HasGpu = sample.GpuPercent.HasValue;
        if (sample.GpuPercent.HasValue)
        {
            GpuValue = sample.GpuPercent.Value;
            GpuText = $"{sample.GpuPercent.Value:0.0}%";
        }

        NetworkDownloadText = NetworkSpeedMonitor.Format(sample.NetworkReceivedBytesPerSecond);
        NetworkUploadText = NetworkSpeedMonitor.Format(sample.NetworkSentBytesPerSecond);

        var uptime = sample.Uptime;
        UptimeText = uptime.TotalDays >= 1
            ? $"已运行 {uptime.TotalDays:0} 天 {uptime.Hours} 小时"
            : uptime.TotalHours >= 1
                ? $"已运行 {uptime.TotalHours:0} 小时 {uptime.Minutes} 分"
                : $"已运行 {uptime.Minutes} 分 {uptime.Seconds} 秒";
    }

    private void PushCpuHistory(double value)
    {
        // 队列头最老、尾最新；移除头、加尾，让折线向左滚动
        if (CpuHistory.Count > 0)
        {
            CpuHistory.RemoveAt(0);
        }

        CpuHistory.Add(new CpuHistoryPoint { Value = value });
    }
}
