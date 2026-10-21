using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyTodo.App.Services;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Services;

namespace StickyTodo.App.ViewModels;

/// <summary>统计范围选项（今天 / 近3天 / 近7天 / 近30天）。</summary>
public sealed partial class TokenRangeOption : ObservableObject
{
    public TokenRangeOption(int days, string name)
    {
        Days = days;
        Name = name;
    }

    public int Days { get; }

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>刷新间隔选项（5 / 15 / 30 秒）。</summary>
public sealed partial class TokenIntervalOption : ObservableObject
{
    public TokenIntervalOption(int seconds)
    {
        Seconds = seconds;
    }

    public int Seconds { get; }

    public string Name => $"{Seconds}s";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Token 用量看板：定时/手动读本机 opencode 数据库，展示汇总、趋势和按模型明细。
/// </summary>
public partial class TokenBoardViewModel : ObservableObject, IAppearanceHost, IDisposable
{
    private readonly ITokenStatsProvider _provider;
    private readonly TodoService _service;
    private readonly AppSettingsService _settings;
    private readonly SkinService _skin;
    private readonly DispatcherTimer _timer;

    private bool _disposed;

    /// <summary>刷新代际号：切换范围时递增，用来丢弃过期的查询结果。</summary>
    private int _fetchGeneration;

    /// <summary>忙的时候被挡下的 silent 刷新，等这次结束后补跑。</summary>
    private bool _pendingSilentRefresh;

    /// <summary>窗口关闭时取消正在跑的查询。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// 每个统计范围的最近一次结果。切回看过的范围时先秒开旧数据，
    /// 再在后台刷新——大范围（近 30 天要扫 100MB+）才不会每次切换都干等。
    /// </summary>
    private readonly Dictionary<int, TokenStats> _cache = new();

    public TokenBoardViewModel(
        ITokenStatsProvider provider,
        TodoService service,
        Board board,
        AppSettingsService settings,
        SkinService skin)
    {
        _provider = provider;
        _service = service;
        _settings = settings;
        _skin = skin;
        Board = board;
        _boardName = board.Name;
        _isTopmost = settings.GetBoardTopmost(board.Id);
        _isLocked = settings.GetBoardLocked(board.Id);
        _windowOpacity = skin.WindowOpacity;

        Ranges.Add(new TokenRangeOption(0, "今天"));
        Ranges.Add(new TokenRangeOption(3, "近3天"));
        Ranges.Add(new TokenRangeOption(7, "近7天"));
        Ranges.Add(new TokenRangeOption(30, "近30天"));

        Intervals.Add(new TokenIntervalOption(5));
        Intervals.Add(new TokenIntervalOption(15));
        Intervals.Add(new TokenIntervalOption(30));

        _rangeDays = settings.TokenRangeDays;
        _refreshSeconds = settings.TokenRefreshSeconds;
        SelectRange(_rangeDays, persist: false);
        SelectInterval(_refreshSeconds, persist: false);

        _sourceText = provider.SourceDescription;
        SourceError = provider.IsAvailable ? null : "找不到 opencode 数据库，请到设置里指定路径";

        _timer = new DispatcherTimer();
        _timer.Tick += async (_, _) => await RefreshAsync();


    }

    public Board Board { get; }

    public string BoardId => Board.Id;

    public ObservableCollection<TokenRangeOption> Ranges { get; } = new();

    public ObservableCollection<TokenIntervalOption> Intervals { get; } = new();

    public ObservableCollection<TokenModelUsageRow> Models { get; } = new();

    [ObservableProperty]
    private string _boardName;

    [ObservableProperty]
    private TokenStats _stats = TokenStats.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string _updatedText = "尚未刷新";

    [ObservableProperty]
    private string _rangeText = string.Empty;

    /// <summary>顶部四个汇总数字。</summary>
    [ObservableProperty]
    private string _totalText = "0";

    [ObservableProperty]
    private string _stepsText = "0 次";

    [ObservableProperty]
    private string _cacheRatioText = "0%";

    [ObservableProperty]
    private string _breakdownText = string.Empty;

    /// <summary>点击趋势图后的明细（空则隐藏明细卡）。</summary>
    [ObservableProperty]
    private string? _detailTitle;

    [ObservableProperty]
    private string? _detailBody;

    [ObservableProperty]
    private bool _hasDetail;

    /// <summary>成功取数的次数。回归用它判断"是否真的又读了一次"。</summary>
    public int FetchCount { get; private set; }

    /// <summary>数据源说明（本机数据库路径），显示在页脚。</summary>
    [ObservableProperty]
    private string _sourceText = string.Empty;

    /// <summary>数据源不可用时的提示（库不存在/读不了）。</summary>
    [ObservableProperty]
    private string? _sourceError;

    /// <summary>当前是否有内容可显示（有缓存或已取过一次）。</summary>
    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private bool? _isEmptyOrError;

    [ObservableProperty]
    private int _selectedIndex = -1;

    private int _rangeDays;

    private int _refreshSeconds;

    /// <summary>当前统计范围（天）。</summary>
    public int RangeDays
    {
        get => _rangeDays;
        private set
        {
            if (SetProperty(ref _rangeDays, value))
            {
                OnPropertyChanged(nameof(Granularity));
            }
        }
    }

    /// <summary>0/3 天用小时粒度，7/30 天用天粒度。</summary>
    public string Granularity => RangeDays <= 3 ? "hour" : "day";

    public int RefreshSeconds
    {
        get => _refreshSeconds;
        private set
        {
            if (SetProperty(ref _refreshSeconds, value))
            {
                OnPropertyChanged(nameof(RefreshHint));
                ApplyTimer();
            }
        }
    }

    public string RefreshHint => $"每 {RefreshSeconds}s 自动刷新";

    public void SelectRange(int days, bool persist = true)
    {
        RangeDays = days;

        foreach (var option in Ranges)
        {
            option.IsSelected = option.Days == days;
        }

        if (persist)
        {
            _settings.TokenRangeDays = days;

            // 有缓存就先铺上，让人立刻看到内容；随后再静默刷新
            if (_cache.TryGetValue(days, out var cached))
            {
                Apply(cached);
                _ = RefreshCoreAsync(silent: true);
                return;
            }

            _ = RefreshAsync();
        }
    }

    public void SelectInterval(int seconds, bool persist = true)
    {
        RefreshSeconds = seconds;

        foreach (var option in Intervals)
        {
            option.IsSelected = option.Seconds == seconds;
        }

        if (persist)
        {
            _settings.TokenRefreshSeconds = seconds;
        }
    }

    [RelayCommand]
    private void PickRange(TokenRangeOption? option)
    {
        if (option is not null)
        {
            SelectRange(option.Days);
        }
    }

    [RelayCommand]
    private void PickInterval(TokenIntervalOption? option)
    {
        if (option is not null)
        {
            SelectInterval(option.Seconds);
        }
    }

    /// <summary>窗口打开时启动自动刷新。</summary>
    public async Task LoadAsync()
    {
        ApplyTimer();
        await RefreshAsync();
    }

    private void ApplyTimer()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(5, RefreshSeconds));
        _timer.Start();
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(silent: false);

    /// <summary>
    /// 取数并应用到界面。
    /// <paramref name="silent"/> = true 用于"已有缓存先显示、后台刷新"的场景：
    /// 不显示忙碌态、失败也不弹错误，避免闪一下。
    /// </summary>
    private async Task RefreshCoreAsync(bool silent)
    {
        if (IsBusy)
        {
            // 正在取数时又来了 silent 请求（切范围先铺缓存那种），
            // 记一笔，等当前这次结束后补一次，别把刷新丢掉。
            if (silent)
            {
                _pendingSilentRefresh = true;
            }

            return;
        }

        IsBusy = true;

        if (!silent)
        {
            ErrorText = null;
        }

        // 每次刷新都带一个新的代际号：切范围时旧查询可能还在跑，
        // 回来晚了就直接丢掉，避免把用户已经切走的范围的结果盖到界面上。
        var generation = ++_fetchGeneration;

        try
        {
            // Microsoft.Data.Sqlite 的 async 其实是同步执行，直接 await 会卡住 UI 线程，
            // 所以显式丢到线程池上跑。
            var days = RangeDays;
            var granularity = Granularity;
            var stats = await Task.Run(() => _provider.FetchAsync(days, granularity), _lifetime.Token)
                .ConfigureAwait(true);

            if (generation != _fetchGeneration)
            {
                return;
            }

            _cache[days] = stats;
            Apply(stats);
        }
        catch (OperationCanceledException)
        {
            // 窗口关了，忽略
        }
        catch (Exception ex)
        {
            if (generation == _fetchGeneration && !silent)
            {
                ErrorText = $"读取失败：{ex.Message}";
                UpdatedText = $"刷新失败 · {DateTime.Now:HH:mm:ss}";
            }
        }
        finally
        {
            if (generation == _fetchGeneration)
            {
                IsBusy = false;

                if (_pendingSilentRefresh && !_disposed)
                {
                    _pendingSilentRefresh = false;
                    _ = RefreshCoreAsync(silent: true);
                }
            }
        }
    }

    private void Apply(TokenStats stats)
    {
        Stats = stats;
        FetchCount++;
        HasData = true;

        TotalText = FormatTokens(stats.Total.Total);
        StepsText = $"{stats.Steps} 次";
        CacheRatioText = $"{stats.Total.CacheRatio * 100:0.0}%";
        BreakdownText =
            $"输入 {FormatTokens(stats.Total.Input)} · 输出 {FormatTokens(stats.Total.Output)} · " +
            $"推理 {FormatTokens(stats.Total.Reasoning)} · 缓存读 {FormatTokens(stats.Total.CacheRead)}";

        RangeText = stats.From == DateTimeOffset.MinValue
            ? string.Empty
            : RangeDays == 0
                ? $"{stats.From:MM-dd} 今天 · {stats.Series.Count(p => p.Total > 0)} 个时段有用量"
                : $"{stats.From:MM-dd} ~ {stats.To:MM-dd} · 活跃 {stats.ActiveDays} 天";

        UpdatedText = $"更新于 {DateTime.Now:HH:mm:ss}";

        Models.Clear();
        foreach (var model in stats.Models.OrderByDescending(m => m.Total))
        {
            Models.Add(new TokenModelUsageRow(model, stats.Total.Total));
        }

        IsEmptyOrError = stats.Total.Total == 0;

        // 刷新（尤其是 5 秒一次）不要把用户点开的明细冲掉，按原下标重新挂上
        if (SelectedIndex >= 0 && SelectedIndex < stats.Series.Count)
        {
            ShowPointDetail(SelectedIndex);
        }
        else
        {
            HideDetail();
        }
    }

    /// <summary>趋势图点击：显示该点的明细。</summary>
    public void ShowPointDetail(int index)
    {
        if (index < 0 || index >= Stats.Series.Count)
        {
            HideDetail();
            return;
        }

        var point = Stats.Series[index];
        SelectedIndex = index;
        DetailTitle = $"{point.Key} · 共 {FormatTokens(point.Total)}";
        DetailBody =
            $"调用 {point.Steps} 次 · 输入 {FormatTokens(point.Usage.Input)} · " +
            $"输出 {FormatTokens(point.Usage.Output)} · 推理 {FormatTokens(point.Usage.Reasoning)} · " +
            $"缓存读 {FormatTokens(point.Usage.CacheRead)} · 缓存比 {point.Usage.CacheRatio * 100:0.0}%";
        HasDetail = true;
    }

    public void HideDetail()
    {
        DetailTitle = null;
        DetailBody = null;
        HasDetail = false;
        SelectedIndex = -1;
    }

    /// <summary>把 12345678 显示成 12.35M 这类好读的写法。</summary>
    public static string FormatTokens(long value)
    {
        if (value >= 100_000_000)
        {
            return $"{value / 100_000_000.0:0.##}亿";
        }

        if (value >= 10_000)
        {
            return $"{value / 10_000.0:0.#}万";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000.0:0.#}K";
        }

        return value.ToString();
    }

    // ---------- 窗口外观（和其它看板一致） ----------

    [ObservableProperty]
    private bool _isTopmost;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private double _windowOpacity = 1.0;

    public string CurrentSkinKey => _skin.Current.Key;

    public double CurrentOpacity => _skin.Opacity;

    public double CurrentWindowOpacity => _skin.WindowOpacity;

    public void ToggleTopmost()
    {
        IsTopmost = !IsTopmost;
        _settings.SetBoardTopmost(BoardId, IsTopmost);
    }

    public void ToggleLock()
    {
        IsLocked = !IsLocked;
        _settings.SetBoardLocked(BoardId, IsLocked);
    }

    public void SetSkin(string skinKey)
    {
        _skin.SetSkin(skinKey);
        OnPropertyChanged(nameof(CurrentSkinKey));
    }

    public void SetOpacity(double opacity)
    {
        _skin.SetOpacity(opacity);
        OnPropertyChanged(nameof(CurrentOpacity));
    }

    public void SetWindowOpacity(double opacity)
    {
        _skin.SetWindowOpacity(opacity);
        WindowOpacity = _skin.WindowOpacity;
        OnPropertyChanged(nameof(CurrentWindowOpacity));
    }

    public void RefreshAppearance()
    {
        OnPropertyChanged(nameof(CurrentSkinKey));
        OnPropertyChanged(nameof(CurrentOpacity));
        OnPropertyChanged(nameof(CurrentWindowOpacity));
        WindowOpacity = _skin.WindowOpacity;
    }

    public async Task RenameBoardAsync(string name)
    {
        try
        {
            await Task.Run(() => _service.RenameBoard(BoardId, name));
            Board.Name = name;
            BoardName = name;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "重命名 Token 看板失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();

        try
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>按模型一行。</summary>
public sealed class TokenModelUsageRow
{
    public TokenModelUsageRow(TokenModelUsage usage, long grandTotal)
    {
        Usage = usage;
        Share = grandTotal <= 0 ? 0 : (double)usage.Total / grandTotal;
    }

    public TokenModelUsage Usage { get; }

    public double Share { get; }

    public string Name => Usage.ModelId;

    public string Provider => Usage.ProviderId;

    public string TotalText => TokenBoardViewModel.FormatTokens(Usage.Total);

    public string StepsText => $"{Usage.Steps} 次";

    public string ShareText => $"{Share * 100:0.#}%";

    public string DetailText =>
        $"输入 {TokenBoardViewModel.FormatTokens(Usage.Usage.Input)} · " +
        $"输出 {TokenBoardViewModel.FormatTokens(Usage.Usage.Output)} · " +
        $"推理 {TokenBoardViewModel.FormatTokens(Usage.Usage.Reasoning)} · " +
        $"缓存 {TokenBoardViewModel.FormatTokens(Usage.Usage.CacheRead)}";
}