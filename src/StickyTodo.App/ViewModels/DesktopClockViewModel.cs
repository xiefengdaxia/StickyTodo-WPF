using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.App.Services;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Calendar;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Weather;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 桌面悬浮时钟（桌面挂件）：阳历 + 农历（含节气/节日）+ 时间，
/// 下面是今日天气和今日日程——点天气行打开天气看板、点日程行打开日历看板。
/// 刻意做成小窗、半透明、置顶、不进任务栏，悬浮在桌面角落。
/// </summary>
public sealed partial class DesktopClockViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private readonly WeatherService _weather;
    private readonly AppSettingsService _settings;
    private readonly TodoService _todos;
    private readonly WindowManager _windows;
    private readonly ITokenStatsProvider _tokens;
    private readonly WeatherCache _cache;

    private IReadOnlyList<string> _cities;
    private int _currentCityIndex;
    private WeatherReport? _report;
    private bool _weatherReady;
    private int _rotation;

    /// <summary>当前显示的是哪一天：跨天后农历、节气、日程都要整体重算。</summary>
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);

    private string _lunarText = string.Empty;

    /// <summary>最后一次取数用的城市（用户改默认城市后要立刻换）。</summary>
    private string _loadedCity = string.Empty;

    /// <summary>时钟窗口本身（点"打开天气/日历"时要贴着它放）。</summary>
    private Window? _owner;

    /// <summary>天气全列表失败后的下次重试时间；null = 不需要急着重试。</summary>
    private DateTime? _failedUntil;

    public DesktopClockViewModel(
        WeatherService weather,
        AppSettingsService settings,
        TodoService todos,
        WindowManager windows,
        ITokenStatsProvider tokens,
        WeatherCache cache)
    {
        _weather = weather;
        _settings = settings;
        _todos = todos;
        _windows = windows;
        _tokens = tokens;
        _cache = cache;
        _cities = settings.WeatherCities;
        _currentCityIndex = DefaultCityIndex();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    /// <summary>窗口建好后把自己交给 ViewModel：点天气/日历时按它的位置摆放。</summary>
    public void AttachWindow(Window window) => _owner = window;

    /// <summary>时钟开始走 + 首次算好农历/日程并加载天气、token。</summary>
    public void Start()
    {
        RefreshClockTexts();
        RefreshDayTexts();
        _ = RefreshTokensAsync();
        _timer.Start();
        _ = LoadWeatherAsync();
        _ = RefreshNetworkSpeedAsync();

        // 看板开了/关了，左侧快捷入口跟着重列
        _windows.BoardsChanged += OnBoardsChanged;

        // 换了看板图标：这一列也要跟着换，不然只有管理面板变了
        _settings.SettingChanged += OnSettingChanged;

        ReloadQuickItems();
    }

    public void Stop()
    {
        _timer.Stop();
        _speedMonitor?.Dispose();
        _speedMonitor = null;
        _windows.BoardsChanged -= OnBoardsChanged;
        _settings.SettingChanged -= OnSettingChanged;
    }

    private void OnBoardsChanged(object? sender, EventArgs e) => ReloadQuickItems();

    /// <summary>只在「某个 AI 看板的图标」被改写时重列（其余设置跟这一列无关）。</summary>
    private void OnSettingChanged(string key)
    {
        if (key.StartsWith("board.", StringComparison.Ordinal)
            && key.EndsWith(".icon", StringComparison.Ordinal))
        {
            ReloadQuickItems();
        }
    }

    // ---------- 每秒刷新的时间文本 ----------

    /// <summary>中文日期格式反复用到，缓存一份（原来每秒钟 new 两次 CultureInfo）。</summary>
    private static readonly System.Globalization.CultureInfo ZhCn =
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN");

    // 三个文本都缓存上一次的值：只有真的变了才通知界面重绘
    private string _solarDateText = string.Empty;
    private string _weekdayText = string.Empty;
    private string _timeText = string.Empty;

    public string SolarDateText => _solarDateText;

    public string WeekdayText => _weekdayText;

    public string TimeText => _timeText;

    /// <summary>
    /// 每秒只重算并比较三个短字符串；日期 / 星期没变就不发通知，
    /// 省掉整个时钟窗口每秒 3 次无谓的布局与重绘。
    /// </summary>
    private void RefreshClockTexts()
    {
        var now = DateTime.Now;

        var solar = now.ToString("yyyy年M月d日", ZhCn);
        if (!string.Equals(solar, _solarDateText, StringComparison.Ordinal))
        {
            _solarDateText = solar;
            OnPropertyChanged(nameof(SolarDateText));
        }

        var weekday = now.ToString("dddd", ZhCn);
        if (!string.Equals(weekday, _weekdayText, StringComparison.Ordinal))
        {
            _weekdayText = weekday;
            OnPropertyChanged(nameof(WeekdayText));
        }

        var time = now.ToString("HH:mm:ss", ZhCn);
        if (!string.Equals(time, _timeText, StringComparison.Ordinal))
        {
            _timeText = time;
            OnPropertyChanged(nameof(TimeText));
        }
    }

    /// <summary>时间行右侧的网速：↓ 下载 / ↑ 上传，单位自动换档。</summary>
    [ObservableProperty]
    private string _networkSpeedText = string.Empty;

    /// <summary>农历 + 干支 + 生肖；今天有节日/节气就追加在末尾（每天只算一次）。</summary>
    public string LunarText => _lunarText;

    // ---------- 今日日程（点一下打开日历） ----------

    [ObservableProperty]
    private string _agendaLine = "正在读取日程…";

    [ObservableProperty]
    private string _agendaDetail = string.Empty;

    /// <summary>今天的 token 用量摘要，如「3.2亿 · 783 次」；没有数据时为空。</summary>
    [ObservableProperty]
    private string _tokenSummary = string.Empty;

    /// <summary>底部那一行的右侧文字：有 token 数据就显示 token，否则退回完成进度。</summary>
    public string BottomRightText => string.IsNullOrEmpty(TokenSummary) ? AgendaDetail : TokenSummary;

    partial void OnTokenSummaryChanged(string value) => OnPropertyChanged(nameof(BottomRightText));

    partial void OnAgendaDetailChanged(string value) => OnPropertyChanged(nameof(BottomRightText));

    /// <summary>取今天的 token 用量（本机各客户端账本，不联网）。</summary>
    private async Task RefreshTokensAsync()
    {
        try
        {
            var stats = await _tokens.FetchAsync(0, "hour").ConfigureAwait(true);
            TokenSummary = stats.Total.Total > 0
                ? $"{TokenBoardViewModel.FormatTokens(stats.Total.Total)} · {stats.Steps:N0} 次"
                : string.Empty;
        }
        catch (Exception)
        {
            // 账本读不到就当作今天没有数据，别让挂件报错
            TokenSummary = string.Empty;
        }
    }

    // ---------- 网速（时间行右侧） ----------

    private Core.Net.NetworkSpeedMonitor? _speedMonitor;

    /// <summary>起一个后台循环：每秒采样一次网速，直到 Stop。计数器不可用就留空。</summary>
    private async Task RefreshNetworkSpeedAsync()
    {
        try
        {
            var monitor = new Core.Net.NetworkSpeedMonitor();
            _speedMonitor = monitor;

            if (!monitor.IsAvailable)
            {
                return;
            }

            // 和时钟同一个 UI 线程上下文，赋值走绑定刷新
            await Task.Yield();

            // 3 秒采一次就够看：网速本身是抖动的，1 秒一刷既没意义，
            // 又因为 NextValue() 每次都要遍历所有网卡实例而白白抬内存。
            const int sampleSeconds = 3;

            while (_speedMonitor == monitor)
            {
                // 读性能计数器是慢操作（每块网卡一次 PDH 调用），放后台线程，
                // 别让时钟在 UI 线程上每秒卡一下
                var (received, sent) = await Task.Run(() => monitor.ReadBytesPerSecond()).ConfigureAwait(true);

                NetworkSpeedText =
                    $"↓{Core.Net.NetworkSpeedMonitor.Format(received)} ↑{Core.Net.NetworkSpeedMonitor.Format(sent)}";

                await Task.Delay(TimeSpan.FromSeconds(sampleSeconds)).ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // 网速显示失败就隐藏，别影响时钟本身
            NetworkSpeedText = string.Empty;
        }
    }

    /// <summary>重新算农历 + 节气/节日 + 今日日程（日程部分异步，不阻塞调用方）。</summary>
    private void RefreshDayTexts()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _day = today;

        _lunarText = BuildLunarText(today);
        OnPropertyChanged(nameof(LunarText));

        _ = RefreshAgendaAsync(today);
    }

    private static string BuildLunarText(DateOnly day)
    {
        var lunar = LunarCalendar.SolarToLunar(day);
        var text = lunar.IsValid
            ? $"农历{lunar.MonthName}月{lunar.DayName} · {LunarCalendar.YearGanZhi(lunar.Year)}年{LunarCalendar.YearZodiac(lunar.Year)}年"
            : string.Empty;

        // 节日优先于节气（节日更容易和"今天的安排"对上）；都没有就不追加
        var almanac = Almanac.Describe(day);
        var extra = !string.IsNullOrEmpty(almanac.HolidayName)
            ? almanac.HolidayName
            : almanac.SolarTerm;

        if (string.IsNullOrEmpty(extra))
        {
            return text;
        }

        return text.Length == 0 ? extra : text + " · " + extra;
    }

    private async Task RefreshAgendaAsync(DateOnly day)
    {
        AgendaSnapshot snapshot;
        try
        {
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
            var to = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue)).AddTicks(-1);
            // 查库 + 重复规则展开都放后台：时钟每 30 秒就要来一遍，
            // 同步跑在 UI 线程上，磁盘一忙时钟就卡（同 ReminderPump 的做法）
            var entries = await Task.Run(() => _todos.GetCalendarEntries(from, to, includeDone: true))
                .ConfigureAwait(true);
            snapshot = AgendaSummary.Build(entries, day, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            AgendaLine = "日程读取失败";
            AgendaDetail = ex.Message;
            return;
        }

        if (snapshot.IsEmpty)
        {
            AgendaLine = "今天没有安排";
            AgendaDetail = string.Empty;   // 打开日历的入口改到日期/农历上了，这里不再放提示
            return;
        }

        if (snapshot.AllDone)
        {
            AgendaLine = $"今日 {snapshot.Total} 项 · 已全部完成";
            AgendaDetail = string.Empty;
            return;
        }

        var next = snapshot.Next!;
        var time = next.Occurrence.LocalDateTime.TimeOfDay == TimeSpan.Zero
            ? string.Empty
            : next.Occurrence.LocalDateTime.ToString("HH:mm") + " ";

        AgendaLine = $"今日 {snapshot.Total} 项 · {time}{Clip(next.Todo.Title)}";
        AgendaDetail = $"已完成 {snapshot.Done}/{snapshot.Total}";
    }

    /// <summary>标题太长会把这一行挤爆，截到 12 个字。</summary>
    private static string Clip(string text, int max = 12)
    {
        var clean = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }

    /// <summary>点天气行：打开天气看板（还没有就建一个）；已经开着就关掉。</summary>
    public void ToggleWeatherBoard() => ToggleBoard(BoardKind.Weather, "天气");

    /// <summary>点日程行：打开日历看板并跳回今天；已经开着就关掉。</summary>
    public void ToggleCalendarBoard() => ToggleBoard(BoardKind.Calendar, "日历");

    /// <summary>点日程行：打开待办看板（默认看板）；已经开着就关掉。</summary>
    public void ToggleAgendaBoard() => ToggleBoard(_todos.EnsureDefaultBoard());

    /// <summary>点 token 区域：打开 token 用量看板；已经开着就关掉。</summary>
    public void ToggleTokenBoard() => ToggleBoard(BoardKind.Token, "Token");

    /// <summary>点时间区域：打开世界时钟；已经开着就关掉。</summary>
    public void ToggleWorldClockBoard() => ToggleBoard(BoardKind.WorldClock, "世界时钟");

    /// <summary>点网速区域：打开网速看板；已经开着就关掉。</summary>
    public void ToggleNetworkSpeedBoard() => ToggleBoard(BoardKind.NetworkSpeed, "网速");

    // ---------------- 左侧快捷入口 ----------------

    /// <summary>
    /// 快捷入口里的一列，固定顺序：便签看板 → 番茄时钟 → 性能看板 → 所有 AI 看板。
    /// 不再列「打开中的看板」——那一列会变来变去，想找的东西每次都换位置。
    /// </summary>
    public ObservableCollection<ClockQuickItem> QuickItems { get; } = new();

    /// <summary>
    /// 图标前景色。这一列的底色固定是深色，浅色皮肤的 Brush.Ink 是近黑色，
    /// 直接用会"图标看不见"：深色皮肤用皮肤的 Ink（本身就是浅色），浅色皮肤用 #E6FFFFFF。
    /// </summary>
    [ObservableProperty]
    private Brush _quickInk = Brushes.White;

    /// <summary>上一次算图标色用的是哪套皮肤：没换皮肤就不重建画刷。</summary>
    private string _quickInkSkin = string.Empty;

    /// <summary>按当前皮肤深浅挑一个能在深色底上看见的图标色。</summary>
    private void RefreshQuickInk()
    {
        var skin = NoteSkins.Resolve(_settings.Skin);
        if (string.Equals(skin.Key, _quickInkSkin, StringComparison.Ordinal))
        {
            return;
        }

        _quickInkSkin = skin.Key;

        var brush = new SolidColorBrush(skin.IsDark ? skin.Ink : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

        brush.Freeze();
        QuickInk = brush;
    }

    /// <summary>重列快捷入口（看板增删、皮肤深浅变了都要重来一遍）。</summary>
    public void ReloadQuickItems()
    {
        RefreshQuickInk();

        QuickItems.Clear();

        // 便签、性能：这类看板没有就建一块、有就置前
        QuickItems.Add(ClockQuickItem.ForKind(BoardKind.Memo, Loc.T("clock.quickMemo")));
        QuickItems.Add(ClockQuickItem.Pomodoro(Loc.T("clock.quickPomodoro")));
        QuickItems.Add(ClockQuickItem.ForKind(BoardKind.Performance, Loc.T("clock.quickPerformance")));

        // 所有 AI 看板：一块一块列出来，点哪个开哪个（图标就是管理面板里选的那个）
        foreach (var board in _todos.GetBoards().Where(b => b.Kind == BoardKind.Custom))
        {
            QuickItems.Add(ClockQuickItem.ForBoard(board, _settings));
        }
    }

    /// <summary>点快捷入口：番茄时钟走托盘那套单例，看板类的没有就建、有就置前。</summary>
    public void OpenQuickItem(ClockQuickItem item)
    {
        switch (item.Action)
        {
            case ClockQuickAction.Pomodoro:
                App.ShowPomodoro();
                return;

            case ClockQuickAction.Board when item.Board is { } board:
                _windows.OpenBoard(board);   // 已开着就只激活
                return;

            default:
                OpenKind(item.Kind, item.Name);
                return;
        }
    }

    /// <summary>按类型打开看板：有就置前，没有就贴到时钟旁边新建一块。</summary>
    private void OpenKind(BoardKind kind, string defaultName)
    {
        var board = _todos.GetBoards().FirstOrDefault(b => b.Kind == kind);

        if (board is not null)
        {
            _windows.OpenBoard(board);
            return;
        }

        _windows.OpenBoardBeside(kind, defaultName, _owner);
    }

    /// <summary>
    /// 贴边打开 / 关闭对应看板：没开就建好并贴在时钟旁边，已经开着就关掉。
    /// </summary>
    private void ToggleBoard(BoardKind kind, string defaultName)
    {
        var board = _todos.GetBoards().FirstOrDefault(b => b.Kind == kind);

        if (board is not null)
        {
            ToggleBoard(board);
            return;
        }

        _windows.OpenBoardBeside(kind, defaultName, _owner);
    }

    /// <summary>已有看板：开着就关，没开就贴边打开。</summary>
    private void ToggleBoard(Board board)
    {
        if (_windows.IsBoardOpen(board.Id))
        {
            _windows.CloseBoard(board.Id);
            return;
        }

        _windows.OpenBoardBeside(board, _owner);
    }

    // ---------- 天气轮播 ----------

    /// <summary>轮播内容：前 9 秒显示天气，之后 3 秒显示城市名，循环。</summary>
    [ObservableProperty]
    private string _weatherLine = "正在获取天气…";

    [ObservableProperty]
    private string _weatherDetail = string.Empty;

    [ObservableProperty]
    private string _cityText = string.Empty;

    private async Task TickAsync()
    {
        RefreshClockTexts();

        _rotation++;

        // 跨天：农历/节气/日程一起换；没跨天就每 30 秒刷一次日程
        if (DateOnly.FromDateTime(DateTime.Now) != _day)
        {
            RefreshDayTexts();
        }
        else if (_rotation % 30 == 0)
        {
            await RefreshAgendaAsync(_day);
        }

        UpdateWeatherLine();

        // token 用量每 2 分钟刷一次（今天的数据一直在变）
        if (_rotation % 120 == 0)
        {
            _ = RefreshTokensAsync();
        }

        // 换了皮肤：快捷入口的图标前景色要跟着深浅走（浅色皮肤的 Ink 在深色底上看不见）
        if (_rotation % 5 == 0)
        {
            RefreshQuickInk();
        }

        // 用户在看板里改了默认城市：不用等 30 分钟，5 秒内就换过来
        // （放在失败重试判断之前：用户主动改的，不该被"60 秒后再试"挡着）
        if (_rotation % 5 == 0
            && !string.Equals(_settings.EffectiveWeatherDefaultCity ?? string.Empty, _loadedCity, StringComparison.Ordinal))
        {
            _failedUntil = null;
            await LoadWeatherAsync();
            return;
        }

        if (_failedUntil is { } until && DateTime.Now < until)
        {
            return;
        }

        // 每 30 分钟刷新天气数据
        if (_rotation % 1800 == 0 || _failedUntil is not null)
        {
            await LoadWeatherAsync();
        }
    }

    private void UpdateWeatherLine()
    {
        if (!_weatherReady || _report is null || _report.Days.Count == 0)
        {
            return;
        }

        // 轮播文案一分钟才变一次：每秒重算字符串是白耗 CPU，算完与现值比较，变了才发通知
        var day = _report.Days.FirstOrDefault(d => d.DayLabel == "今天") ?? _report.Days[0];
        var line = $"{_report.CityName} {day.TempMax:0}°C {day.Text}";
        var detail = BuildWeatherDetail(day);

        if (line != WeatherLine)
        {
            WeatherLine = line;
        }

        if (detail != WeatherDetail)
        {
            WeatherDetail = detail;
        }
    }

    /// <summary>风况 + 降水（概率高才提）或湿度，一行放得下。</summary>
    private string BuildWeatherDetail(WeatherDayInfo day)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(day.Wind))
        {
            parts.Add(day.Wind);
        }

        if (day.PrecipitationProbability >= 30)
        {
            parts.Add($"降水{day.PrecipitationProbability}%");
        }
        else if (_report?.Humidity is { } humidity)
        {
            parts.Add($"湿度{humidity}%");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>回归用：当前取数用的城市。</summary>
    internal string LoadedCityForTest => _loadedCity;

    /// <summary>默认城市在列表里的下标（没设置 / 找不到就是 0）。</summary>
    private int DefaultCityIndex()    {
        var defaultCity = _settings.EffectiveWeatherDefaultCity;
        if (string.IsNullOrEmpty(defaultCity))
        {
            return 0;
        }

        for (var i = 0; i < _cities.Count; i++)
        {
            if (string.Equals(_cities[i], defaultCity, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    private async Task LoadWeatherAsync()
    {
        // 每次都重新读设置：用户在天气看板里加/删城市、改默认城市后，立刻跟上
        _cities = _settings.WeatherCities;
        _currentCityIndex = DefaultCityIndex();

        if (_cities.Count == 0)
        {
            WeatherLine = "还没有城市";
            WeatherDetail = "在天气看板里添加";
            return;
        }

        // 只看默认城市：查不到就明确说"暂无数据"，不拿别的城市的天气顶上
        var city = _cities[Math.Min(_currentCityIndex, _cities.Count - 1)];
        _loadedCity = city;
        var outcome = await _weather.FetchWithOutcomeAsync(city);

        if (outcome.Success && outcome.Report is { Days.Count: > 0 })
        {
            _report = outcome.Report;
            _weatherReady = true;
            _failedUntil = null;
            _cache.Set(city, outcome.Report, DateTime.Now);   // 天气看板打开时直接用这份
            UpdateWeatherLine();
            return;
        }

        // 失败：清掉旧数据，显示"暂无"，60 秒后再试
        _report = null;
        _weatherReady = false;
        _failedUntil = DateTime.Now.AddSeconds(60);
        WeatherLine = $"{city} 暂无天气数据";
        WeatherDetail = outcome.Error ?? "60 秒后自动重试";
    }
}

/// <summary>快捷入口点下去做什么。</summary>
public enum ClockQuickAction
{
    /// <summary>按类型打开看板（没有就建一块）。</summary>
    Kind,

    /// <summary>打开某一块具体的看板（AI 看板）。</summary>
    Board,

    /// <summary>打开番茄时钟。</summary>
    Pomodoro,
}

/// <summary>桌面时钟左侧快捷入口的一行：类型图标 + 名称 + 点下去要做什么。</summary>
public sealed class ClockQuickItem
{
    private ClockQuickItem(string name, BoardGlyph icon, ClockQuickAction action, BoardKind kind, Board? board)
    {
        Name = name;
        Icon = icon;
        Action = action;
        Kind = kind;
        Board = board;
    }

    /// <summary>按看板类型建一行（图标走 <see cref="BoardIcons"/>，和管理面板同一份）。</summary>
    public static ClockQuickItem ForKind(BoardKind kind, string name) =>
        new(name, BoardIcons.ForKind(kind), ClockQuickAction.Kind, kind, board: null);

    /// <summary>
    /// 某一块具体的看板：AI 看板用它自己选的图标（和设置里存的是同一个 key），
    /// 换图标后管理面板和这一列画出来的仍是同一个字形。
    /// </summary>
    public static ClockQuickItem ForBoard(Board board, AppSettingsService? settings = null) =>
        new(
            board.Name,
            board.Kind == BoardKind.Custom && settings is not null
                ? BoardIcons.Resolve(settings.GetBoardIcon(board.Id))
                : BoardIcons.ForKind(board.Kind),
            ClockQuickAction.Board,
            board.Kind,
            board);

    /// <summary>番茄时钟：窗口不在看板体系里，单列一种动作。</summary>
    public static ClockQuickItem Pomodoro(string name) =>
        new(name, BoardIcons.Pomodoro, ClockQuickAction.Pomodoro, BoardKind.Todo, board: null);

    public Board? Board { get; }

    public string Name { get; }

    public BoardGlyph Icon { get; }

    public string Glyph => Icon.Glyph;

    /// <summary>画 <see cref="Glyph"/> 用的字体（单色符号字体）。</summary>
    public FontFamily GlyphFont => Icon.Font;

    public ClockQuickAction Action { get; }

    /// <summary>动作是「按类型打开」时用哪个类型。</summary>
    public BoardKind Kind { get; }
}
