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
/// Token 用量看板：定时/手动向上游面板拉数据，展示汇总、趋势和按模型明细。
/// </summary>
public partial class TokenBoardViewModel : ObservableObject, IAppearanceHost, IDisposable
{
    private readonly ITokenStatsProvider _provider;
    private readonly TodoService _service;
    private readonly AppSettingsService _settings;
    private readonly SkinService _skin;
    private readonly HttpTokenStatsProvider? _httpProvider;
    private readonly DispatcherTimer _timer;

    private bool _disposed;

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
        _httpProvider = provider as HttpTokenStatsProvider;
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

        _timer = new DispatcherTimer();
        _timer.Tick += async (_, _) => await RefreshAsync();

        if (_httpProvider is not null)
        {
            _httpProvider.Endpoint = settings.TokenEndpoint;
        }
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

    /// <summary>成功取数的次数。回归用它判断"是否真的又请求了一次"。</summary>
    public int FetchCount { get; private set; }

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
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;

        try
        {
            if (_httpProvider is not null)
            {
                _httpProvider.Endpoint = _settings.TokenEndpoint;
            }

            var stats = await _provider.FetchAsync(RangeDays, Granularity);
            Apply(stats);
        }
        catch (Exception ex)
        {
            ErrorText = $"取数失败：{ex.Message}";
            UpdatedText = $"刷新失败 · {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(TokenStats stats)
    {
        Stats = stats;
        FetchCount++;

        TotalText = FormatTokens(stats.Total.Total);
        StepsText = $"{stats.Steps} 次";
        CacheRatioText = $"{stats.Total.CacheRatio * 100:0.0}%";
        BreakdownText =
            $"输入 {FormatTokens(stats.Total.Input)} · 输出 {FormatTokens(stats.Total.Output)} · " +
            $"推理 {FormatTokens(stats.Total.Reasoning)} · 缓存读 {FormatTokens(stats.Total.CacheRead)}";

        RangeText = stats.From == DateTimeOffset.MinValue
            ? string.Empty
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
        _disposed = true;
        _timer.Stop();
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