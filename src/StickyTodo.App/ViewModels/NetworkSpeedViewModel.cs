using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Net;

namespace StickyTodo.App.ViewModels;

/// <summary>网速看板里的一行：一个进程的实时速率。</summary>
public sealed class ProcessSpeedRow : ObservableObject
{
    private string _name = string.Empty;
    private string _received = string.Empty;
    private string _sent = string.Empty;
    private string _total = string.Empty;
    private bool _isSelf;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>下载速率文本。</summary>
    public string Received
    {
        get => _received;
        set => SetProperty(ref _received, value);
    }

    /// <summary>上传速率文本。</summary>
    public string Sent
    {
        get => _sent;
        set => SetProperty(ref _sent, value);
    }

    /// <summary>下载 + 上传（用于排序栏展示总量）。</summary>
    public string Total
    {
        get => _total;
        set => SetProperty(ref _total, value);
    }

    /// <summary>是不是本应用（高亮显示，方便认出"我自己在跑"）。</summary>
    public bool IsSelf
    {
        get => _isSelf;
        set => SetProperty(ref _isSelf, value);
    }
}

/// <summary>
/// 网速看板：按进程展示实时下载 / 实时上传，上下两组，每秒刷新，各自按速率排序。
/// 数据来自 <see cref="ProcessNetworkMonitor"/>（连接表归因 + 全机速率分摊，近似值）。
/// </summary>
public sealed partial class NetworkSpeedViewModel : ObservableObject
{
    private readonly ProcessNetworkMonitor _monitor = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, ProcessSpeedRow> _downloadRows = new();
    private readonly Dictionary<int, ProcessSpeedRow> _uploadRows = new();
    private readonly int _selfPid;
    private bool _sampling;

    /// <summary>全机合计（与桌面时钟挂件同源）。</summary>
    [ObservableProperty]
    private string _totalText = "采集中…";

    public NetworkSpeedViewModel()
    {
        _selfPid = Environment.ProcessId;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>下载组（按下载速度降序）。</summary>
    public ObservableCollection<ProcessSpeedRow> DownloadRows { get; } = new();

    /// <summary>上传组（按上传速度降序）。</summary>
    public ObservableCollection<ProcessSpeedRow> UploadRows { get; } = new();

    /// <summary>窗口打开时开始采样。</summary>
    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    /// <summary>窗口关闭时停采样并释放计数器。</summary>
    public void Stop()
    {
        _timer.Stop();
        _monitor.Dispose();
    }

    /// <summary>当前机器上有没有可用的计数器（没有就整板显示提示）。</summary>
    public bool IsAvailable => _monitor.IsAvailable;

    /// <summary>
    /// 窗口是不是当前激活窗口（由窗口的 Activated/Deactivated 维护）。
    /// 没人在看时把「枚举连接表 + 解析进程名」这套降到 1/3，省 CPU 与内存。
    /// </summary>
    public bool IsActive { get; set; } = true;

    private int _idleTicks;

    private void Refresh()
    {
        // 不在前台时每 3 秒算一次进程明细就够；顶部全机合计仍然每秒更新（很便宜）
        if (!IsActive && ++_idleTicks % 3 != 0)
        {
            var (quickReceived, quickSent) = _monitor.ReadTotal();
            TotalText = $"↓ {NetworkSpeedMonitor.Format(quickReceived)}   ↑ {NetworkSpeedMonitor.Format(quickSent)}";
            return;
        }
        // 全机合计直接用本轮计数器原始值——它和桌面时钟挂件同一来源（Network Interface 计数器），
        // 两边显示的数字因此严格相等。
        // 不能拿列表行求和：行有 1 KB/s 过滤阈值 + 每行 0 位小数取整，
        // 几十行加起来会和全机值差出一大截（就是"对不上"的原因）。
        var (monitorTotalReceived, monitorTotalSent) = _monitor.ReadTotal();
        TotalText = $"↓ {NetworkSpeedMonitor.Format(monitorTotalReceived)}   ↑ {NetworkSpeedMonitor.Format(monitorTotalSent)}";

        if (!_monitor.IsAvailable || _sampling)
        {
            return;
        }

        // 连接表 + 进程名解析在首次要 800ms+，全在 UI 线程会让拖动卡；
        // 挪到后台，算完把结果抛回 UI（_sampling 防上一轮没算完就重入）
        _sampling = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var (download, upload) = _monitor.Read(reuseLastTotals: true);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _sampling = false;
                    FillGroup(DownloadRows, _downloadRows, download, sortKey: s => s.Received);
                    FillGroup(UploadRows, _uploadRows, upload, sortKey: s => s.Sent);
                });
            }
            catch (Exception)
            {
                _sampling = false;
            }
        });
    }

    private void FillGroup(
        ObservableCollection<ProcessSpeedRow> collection,
        Dictionary<int, ProcessSpeedRow> cache,
        IReadOnlyList<ProcessSpeed> speeds,
        Func<ProcessSpeed, double> sortKey)
    {
        var seen = new HashSet<int>();
        var rowsByKey = new Dictionary<int, ProcessSpeedRow>();

        foreach (var speed in speeds)
        {
            seen.Add(speed.ProcessId);

            if (!cache.TryGetValue(speed.ProcessId, out var row))
            {
                row = new ProcessSpeedRow
                {
                    Name = speed.ProcessName,
                    IsSelf = speed.ProcessId == _selfPid,
                };
                cache[speed.ProcessId] = row;
            }

            row.Received = NetworkSpeedMonitor.Format(speed.Received);
            row.Sent = NetworkSpeedMonitor.Format(speed.Sent, precise: true);
            row.Total = NetworkSpeedMonitor.Format(speed.Received + speed.Sent);
            rowsByKey[speed.ProcessId] = row;
        }

        // 重排：降序（速率文本不好反向比较，直接用刚采样的数据排）
        var ordered = speeds.OrderByDescending(sortKey).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var row = rowsByKey[ordered[i].ProcessId];
            var index = collection.IndexOf(row);
            if (index != i)
            {
                if (index >= 0)
                {
                    collection.Move(index, i);
                }
                else
                {
                    collection.Insert(i, row);
                }
            }
        }

        // 消失的进程（退出 / 速率归零）从组里去掉
        for (var i = collection.Count - 1; i >= 0; i--)
        {
            var stale = collection[i];
            if (!seen.Contains(cache.First(kv => ReferenceEquals(kv.Value, stale)).Key))
            {
                var key = cache.First(kv => ReferenceEquals(kv.Value, stale)).Key;
                cache.Remove(key);
                collection.RemoveAt(i);
            }
        }
    }
}
