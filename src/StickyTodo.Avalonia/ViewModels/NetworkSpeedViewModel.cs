using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Net;

namespace StickyTodo.Notes.ViewModels;

/// <summary>网速看板里的一行：一个进程的实时速率。</summary>
public sealed class ProcessSpeedRow : ObservableObject
{
    private string _name = string.Empty;
    private string _received = string.Empty;
    private string _sent = string.Empty;
    private bool _isSelf;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Received
    {
        get => _received;
        set => SetProperty(ref _received, value);
    }

    public string Sent
    {
        get => _sent;
        set => SetProperty(ref _sent, value);
    }

    /// <summary>是不是本应用（高亮显示）。</summary>
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

    /// <summary>当前机器上有没有可用的计数器（没有就整板显示提示）。</summary>
    public bool IsAvailable => _monitor.IsAvailable;

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

    private void Refresh()
    {
        if (!_monitor.IsAvailable)
        {
            TotalText = "—";
            return;
        }

        var (download, upload) = _monitor.Read();

        double totalReceived = 0, totalSent = 0;
        foreach (var s in download)
        {
            totalReceived += s.Received;
        }

        foreach (var s in upload)
        {
            totalSent += s.Sent;
        }

        TotalText = $"↓ {NetworkSpeedMonitor.Format(totalReceived)}   ↑ {NetworkSpeedMonitor.Format(totalSent)}";

        FillGroup(DownloadRows, _downloadRows, download, sortKey: s => s.Received);
        FillGroup(UploadRows, _uploadRows, upload, sortKey: s => s.Sent);
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
            row.Sent = NetworkSpeedMonitor.Format(speed.Sent);
            rowsByKey[speed.ProcessId] = row;
        }

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
            var key = -1;
            foreach (var kv in cache)
            {
                if (ReferenceEquals(kv.Value, stale))
                {
                    key = kv.Key;
                    break;
                }
            }

            if (key >= 0 && !seen.Contains(key))
            {
                cache.Remove(key);
                collection.RemoveAt(i);
            }
        }
    }
}
