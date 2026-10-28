using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Infrastructure.Token;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.ViewModels;

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

/// <summary>Agent 客户端选项（下拉框："全部" / 各客户端）。</summary>
public sealed partial class TokenAgentOption : ObservableObject
{
    public TokenAgentOption(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>"all" 或 agent id（zcode / codex…）。设置持久化用。</summary>
    public string Id { get; }

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Token 用量看板：定时/手动读本机 opencode 数据库，展示汇总、趋势和按模型明细。
/// </summary>
public partial class TokenBoardViewModel : ObservableObject, IAppearanceHost, IRefreshLocalized, IDisposable
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
    private readonly Dictionary<(int Days, string Agent), TokenStats> _cache = new();

    /// <summary>客户端下拉选项（全部 + 检测到的）。</summary>
    public ObservableCollection<TokenAgentOption> Agents { get; } = new();

    [ObservableProperty]
    private TokenAgentOption? _selectedAgent;

    /// <summary>当前客户端筛选（null = 全部）。</summary>
    private string? AgentFilter => SelectedAgent?.Id is { } id && id != "all" ? id : null;

    /// <summary>是否显示客户端下拉（provider 是多 Agent 聚合时才有）。</summary>
    public bool HasAgentPicker => Agents.Count > 0;

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

        Ranges.Add(new TokenRangeOption(0, Loc.T("token.rangeToday")));
        Ranges.Add(new TokenRangeOption(3, Loc.T("token.range3")));
        Ranges.Add(new TokenRangeOption(7, Loc.T("token.range7")));
        Ranges.Add(new TokenRangeOption(30, Loc.T("token.range30")));

        Intervals.Add(new TokenIntervalOption(5));
        Intervals.Add(new TokenIntervalOption(15));
        Intervals.Add(new TokenIntervalOption(30));

        _rangeDays = settings.TokenRangeDays;
        _refreshSeconds = settings.TokenRefreshSeconds;
        SelectRange(_rangeDays, persist: false);
        SelectInterval(_refreshSeconds, persist: false);

        // 客户端下拉：全部 + 各数据源（不可用的标注出来）。非聚合 provider（demo）不填。
        if (provider is MultiAgentTokenStatsProvider multi)
        {
            Agents.Add(new TokenAgentOption("all", Loc.T("token.agentAll")));
            foreach (var source in multi.Sources)
            {
                Agents.Add(new TokenAgentOption(
                    source.AgentId,
                    source.IsAvailable ? source.DisplayName : $"{source.DisplayName}（未检测到）"));
            }

            var saved = settings.TokenAgent;
            _selectedAgent = Agents.FirstOrDefault(a => a.Id == saved) ?? Agents[0];
            multi.SetAgentFilter(AgentFilter);
            foreach (var option in Agents)
            {
                option.IsSelected = ReferenceEquals(option, SelectedAgent);
            }

            OnPropertyChanged(nameof(HasAgentPicker));
        }

        _sourceText = provider.SourceDescription;
        SourceError = provider.IsAvailable ? null : Loc.T("token.sourceMissing");

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
    private string _updatedText = Loc.T("token.never");

    [ObservableProperty]
    private string _rangeText = string.Empty;

    /// <summary>顶部四个汇总数字。</summary>
    [ObservableProperty]
    private string _totalText = "0";

    [ObservableProperty]
    private string _stepsText = "-";

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

    public string RefreshHint => Loc.F("token.refreshHint", RefreshSeconds);

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
            SelectRangeCore(days);
        }
    }

    /// <summary>切客户端：清空当前范围的旧缓存展示，重新取数。</summary>
    public void SelectAgent(TokenAgentOption? option)
    {
        if (option is null || ReferenceEquals(option, SelectedAgent))
        {
            return;
        }

        SelectedAgent = option;
        ApplyAgentChange(option);
    }

    /// <summary>
    /// 应用客户端切换的实际逻辑。UI 的 SelectionChanged 在 TwoWay 绑定写回
    /// SelectedAgent 后触发，那时判等会短路，所以从绑定回调里要走这个方法。
    /// </summary>
    public void ApplyAgentChange(TokenAgentOption option)
    {
        foreach (var item in Agents)
        {
            item.IsSelected = ReferenceEquals(item, option);
        }

        _settings.TokenAgent = option.Id;

        if (_provider is MultiAgentTokenStatsProvider multi)
        {
            multi.SetAgentFilter(AgentFilter);
            SourceText = multi.SourceDescription;
        }

        SelectRangeCore(RangeDays);
    }

    /// <summary>按当前范围取数：有缓存先铺上再后台刷新，否则直接取。</summary>
    private void SelectRangeCore(int days)
    {
        // 有缓存就先铺上，让人立刻看到内容；随后再静默刷新
        if (_cache.TryGetValue((days, AgentFilter ?? "all"), out var cached))
        {
            Apply(cached);
            _ = RefreshCoreAsync(silent: true);
            return;
        }

        _ = RefreshAsync();
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
            // SQLite 的 async 其实是同步执行，直接 await 会卡住 UI 线程，
            // 所以显式丢到线程池上跑。
            var days = RangeDays;
            var granularity = Granularity;
            var stats = await Task.Run(() => _provider.FetchAsync(days, granularity), _lifetime.Token)
                .ConfigureAwait(true);

            if (generation != _fetchGeneration)
            {
                return;
            }

            _cache[(days, AgentFilter ?? "all")] = stats;
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
                ErrorText = Loc.F("token.readFailed", ex.Message);
                UpdatedText = Loc.F("token.refreshFailed", DateTime.Now.ToString("HH:mm:ss"));
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
        StepsText = stats.Steps.ToString();
        CacheRatioText = $"{stats.Total.CacheRatio * 100:0.0}%";
        BreakdownText = Loc.F(
            "token.breakdown",
            FormatTokens(stats.Total.Input),
            FormatTokens(stats.Total.Output),
            FormatTokens(stats.Total.Reasoning),
            FormatTokens(stats.Total.CacheRead));

        RangeText = stats.From == DateTimeOffset.MinValue
            ? string.Empty
            : RangeDays == 0
                ? Loc.F("token.rangeTodayMeta", stats.From.ToString("MM-dd"), stats.Series.Count(p => p.Total > 0))
                : Loc.F("token.rangeMeta", stats.From.ToString("MM-dd"), stats.To.ToString("MM-dd"), stats.ActiveDays);

        UpdatedText = Loc.F("token.updatedAt", DateTime.Now.ToString("HH:mm:ss"));

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

    /// <summary>复制到剪贴板的摘要文本（贴合当前统计范围）。</summary>
    [ObservableProperty]
    private string? _copyStatusText;

    /// <summary>复制成功后的提示自动消失计时。</summary>
    private DispatcherTimer? _copyStatusTimer;

    private Window? _ownerWindow;

    /// <summary>窗口构造后把自身交给 ViewModel，供剪贴板访问。</summary>
    public void AttachOwner(Window owner) => _ownerWindow = owner;

    /// <summary>
    /// 把当前用量整理成文本并复制到剪贴板。
    /// 格式是面向"粘贴给别人看"的：Markdown 汇总 + 按模型明细表。
    /// </summary>
    [RelayCommand]
    private void CopyToClipboard()
    {
        var clipboard = _ownerWindow is null ? null : TopLevel.GetTopLevel(_ownerWindow)?.Clipboard;
        if (clipboard is null)
        {
            CopyStatusText = Loc.T("token.copyFailed");
            return;
        }

        var text = BuildCopyText();
        if (text.Length == 0)
        {
            CopyStatusText = Loc.T("token.copyEmpty");
            return;
        }

        _ = clipboard.SetTextAsync(text);
        CopyStatusText = Loc.F("token.copied", DateTime.Now.ToString("HH:mm"));

        // 提示 3 秒后自动消失
        if (_copyStatusTimer is null)
        {
            _copyStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _copyStatusTimer.Tick += (_, _) =>
            {
                _copyStatusTimer.Stop();
                CopyStatusText = null;
            };
        }

        _copyStatusTimer.Stop();
        _copyStatusTimer.Start();
    }

    /// <summary>组装复制文本。没有数据时返回空串。</summary>
    private string BuildCopyText()
    {
        var stats = Stats;
        if (stats.Total.Total == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine(Loc.F("token.copySummary", RangeDays == 0 ? Loc.T("token.rangeToday") : $"{stats.From:MM-dd} ~ {stats.To:MM-dd}"));
        sb.AppendLine(Loc.F("token.copyAgent", SelectedAgent is { Id: not "all" } a ? a.Name : Loc.T("token.agentAll")));
        sb.AppendLine();
        sb.AppendLine(Loc.F("token.copyTotal", stats.Total.Total.ToString("N0"), stats.Steps.ToString("N0")));
        sb.AppendLine(Loc.F(
            "token.copyBreakdown",
            stats.Total.Input.ToString("N0"),
            stats.Total.Output.ToString("N0"),
            stats.Total.Reasoning.ToString("N0"),
            stats.Total.CacheRead.ToString("N0")));
        sb.AppendLine(Loc.F("token.copyCacheRatio", $"{stats.Total.CacheRatio * 100:0.0}%"));

        if (Models.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Loc.T("token.copyModels"));
            foreach (var row in Models)
            {
                sb.AppendLine(Loc.F(
                    "token.copyModelRow",
                    row.Name,
                    row.Usage.Total.ToString("N0"),
                    (row.Share * 100).ToString("0.#")));
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>趋势图点击：显示该点的明细。</summary>
    public void ShowPointDetail(int index)    {
        if (index < 0 || index >= Stats.Series.Count)
        {
            HideDetail();
            return;
        }

        var point = Stats.Series[index];
        SelectedIndex = index;
        DetailTitle = Loc.F("token.detailTitle", point.Key, FormatTokens(point.Total));
        DetailBody = Loc.F(
            "token.detailBody",
            point.Steps,
            FormatTokens(point.Usage.Input),
            FormatTokens(point.Usage.Output),
            FormatTokens(point.Usage.Reasoning),
            FormatTokens(point.Usage.CacheRead),
            $"{point.Usage.CacheRatio * 100:0.0}%");
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
        // 中文用「亿/万」，其它语言用国际单位 B/M/K，避免出现 "1.2亿" 这种看不懂的写法
        var isChinese = Loc.Language == AppLanguage.ChineseSimplified;

        if (isChinese)
        {
            if (value >= 100_000_000)
            {
                return $"{value / 100_000_000.0:0.##}亿";
            }

            if (value >= 10_000)
            {
                return $"{value / 10_000.0:0.#}万";
            }
        }
        else
        {
            if (value >= 1_000_000_000)
            {
                return $"{value / 1_000_000_000.0:0.##}B";
            }

            if (value >= 1_000_000)
            {
                return $"{value / 1_000_000.0:0.##}M";
            }
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000.0:0.#}K";
        }

        return value.ToString();
    }

    /// <summary>语言切换后重算所有由 C# 拼出来的文案。</summary>
    public void RefreshLocalizedText()
    {
        UpdatedText = Loc.F("token.updatedAt", DateTime.Now.ToString("HH:mm:ss"));
        RangeText = Stats.From == DateTimeOffset.MinValue ? string.Empty : RangeText;
        OnPropertyChanged(nameof(RefreshHint));
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
            Log.Debug("重命名 Token 看板失败：" + ex.Message);
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
        _copyStatusTimer?.Stop();

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

    public string StepsText => Usage.Steps.ToString();

    public string ShareText => $"{Share * 100:0.#}%";

    public string DetailText => Loc.F(
        "token.modelDetail",
        TokenBoardViewModel.FormatTokens(Usage.Usage.Input),
        TokenBoardViewModel.FormatTokens(Usage.Usage.Output),
        TokenBoardViewModel.FormatTokens(Usage.Usage.Reasoning),
        TokenBoardViewModel.FormatTokens(Usage.Usage.CacheRead));
}
