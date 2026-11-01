using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Hardware;
using StickyTodo.Core.Net;

namespace StickyTodo.Notes.ViewModels;

/// <summary>当前皮肤的强调色（画 CPU 折线用）。</summary>
public static class PerformanceColors
{
    public static Avalonia.Media.Color GetAccentColor() =>
        App.Services?.Skin.Current.Accent ?? Avalonia.Media.Color.FromRgb(0xB2, 0x7F, 0x0A);
}

/// <summary>
/// PC 性能看板：CPU（60 秒折线）/ 内存 / 磁盘 IO / GPU / 网络 / 开机时长，
/// 每秒刷新。数据来自 <see cref="HardwareMonitor"/>（性能计数器，纯本机）。
/// CPU 历史点存这里，窗口侧用它画折线。
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    private readonly HardwareMonitor _monitor = new();
    private readonly DispatcherTimer _timer;
    private readonly List<double> _cpuHistory = [];

    /// <summary>CPU 折线的历史长度。</summary>
    public const int CpuHistoryLength = 60;

    [ObservableProperty]
    private string _cpuText = "0%";

    [ObservableProperty]
    private string _memoryText = "—";

    [ObservableProperty]
    private double _memoryValue;

    /// <summary>内存条颜色档位：0 正常，1 偏高（75%），2 告警（90%）。</summary>
    [ObservableProperty]
    private int _memoryLevel;

    [ObservableProperty]
    private string _diskActiveText = "0%";

    [ObservableProperty]
    private double _diskActiveValue;

    [ObservableProperty]
    private string _diskIoText = "↓ 0 KB/s   ↑ 0 KB/s";

    [ObservableProperty]
    private string _gpuText = string.Empty;

    /// <summary>GPU 块是否显示（计数器取不到就隐藏整块）。</summary>
    [ObservableProperty]
    private bool _hasGpu = true;

    [ObservableProperty]
    private string _networkText = "↓ 0 KB/s   ↑ 0 KB/s";

    [ObservableProperty]
    private string _uptimeText = string.Empty;

    public PerformanceViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>CPU 历史值（最新在末尾），窗口用它画折线。</summary>
    public IReadOnlyList<double> CpuHistory => _cpuHistory;

    /// <summary>计数器是否可用。</summary>
    public bool IsAvailable => _monitor.IsAvailable;    /// <summary>窗口打开时开始采样。</summary>
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
        CpuText = $"{sample.CpuPercent:0.0}%";
        _cpuHistory.Add(sample.CpuPercent);
        while (_cpuHistory.Count > CpuHistoryLength)
        {
            _cpuHistory.RemoveAt(0);
        }

        MemoryValue = sample.MemoryPercent;
        MemoryText = sample.MemoryText;
        MemoryLevel = sample.MemoryPercent >= 90 ? 2 : sample.MemoryPercent >= 75 ? 1 : 0;

        DiskActiveValue = sample.DiskActivePercent;
        DiskActiveText = $"{sample.DiskActivePercent:0.0}%";
        DiskIoText = $"↓ {NetworkSpeedMonitor.Format(sample.DiskReadBytesPerSecond)}   ↑ {NetworkSpeedMonitor.Format(sample.DiskWriteBytesPerSecond)}";

        HasGpu = sample.GpuPercent.HasValue;
        if (sample.GpuPercent.HasValue)
        {
            GpuText = $"{sample.GpuPercent.Value:0.0}%";
        }

        NetworkText = $"↓ {NetworkSpeedMonitor.Format(sample.NetworkReceivedBytesPerSecond)}   ↑ {NetworkSpeedMonitor.Format(sample.NetworkSentBytesPerSecond)}";

        var uptime = sample.Uptime;
        UptimeText = uptime.TotalDays >= 1
            ? $"已运行 {uptime.TotalDays:0} 天 {uptime.Hours} 小时"
            : uptime.TotalHours >= 1
                ? $"已运行 {uptime.TotalHours:0} 小时 {uptime.Minutes} 分"
                : $"已运行 {uptime.Minutes} 分 {uptime.Seconds} 秒";

        OnPropertyChanged(nameof(CpuHistory));
    }
}
