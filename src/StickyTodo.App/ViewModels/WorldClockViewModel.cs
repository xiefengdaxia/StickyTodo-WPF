using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.WorldClock;

namespace StickyTodo.App.ViewModels;

/// <summary>世界时钟的一行：一个国家/地区 + 代表城市 + 实时时间。</summary>
public sealed class WorldClockRow : ObservableObject
{
    public WorldClockRow(WorldClockCity city) => City = city;

    public WorldClockCity City { get; }

    public string CityName => City.City;

    public string CountryName => City.Country;

    /// <summary>实时偏移标签，夏令时期间会变（伦敦夏天是 UTC+1）。</summary>
    public string OffsetText { get; private set; } = string.Empty;

    public string TimeText { get; private set; } = string.Empty;

    public string DateText { get; private set; } = string.Empty;

    /// <summary>这一秒的真实显示文本（秒针每秒只变一次，值没变就不发通知，省 96 次/秒的绑定刷新）。</summary>
    private string _lastStamp = string.Empty;

    public void Refresh(DateTime utcNow)
    {
        var local = WorldClockTime.LocalTimeAt(utcNow, City);

        // HH:mm:ss 一秒一变；DateText 大多数时候不变
        var stamp = local.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (stamp == _lastStamp)
        {
            return;
        }

        _lastStamp = stamp;

        OffsetText = WorldClockTime.FormatOffsetLabel(WorldClockTime.OffsetAt(utcNow, City));
        TimeText = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        DateText = local.ToString("M月d日 ddd", new CultureInfo("zh-CN"));

        OnPropertyChanged(nameof(OffsetText));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DateText));
    }
}

/// <summary>
/// 世界时钟看板：<see cref="WorldClockCatalog"/> 里的 32 个时区代表城市，
/// 从 UTC−11 排到 UTC+14，一个定时器统一刷新。
/// </summary>
public sealed class WorldClockViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;

    public WorldClockViewModel()
    {
        foreach (var city in WorldClockCatalog.All)
        {
            Clocks.Add(new WorldClockRow(city));
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public ObservableCollection<WorldClockRow> Clocks { get; } = new();

    /// <summary>立刻刷新一遍（切换看板/打开窗口时用）。</summary>
    public void Refresh()
    {
        var utc = DateTime.UtcNow;
        foreach (var row in Clocks)
        {
            row.Refresh(utc);
        }
    }

    /// <summary>窗口打开时启动秒针（单例 VM 会被多个窗口复用）。</summary>
    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    /// <summary>窗口关闭时停表，避免后台一直跑。</summary>
    public void Stop() => _timer.Stop();
}
