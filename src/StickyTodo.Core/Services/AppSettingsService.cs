using System.Globalization;
using StickyTodo.Core.Abstractions;

namespace StickyTodo.Core.Services;

/// <summary>
/// 外观与窗口偏好。存在通用的 setting 键值表里，加开关不需要改表结构。
/// </summary>
public sealed class AppSettingsService
{
    public const string SkinKey = "appearance.skin";
    public const string LanguageKey = "appearance.language";
    public const string OpacityKey = "appearance.opacity";
    public const string WindowOpacityKey = "appearance.windowOpacity";

    public const string DefaultSkin = "sticky-yellow";
    public const double DefaultOpacity = 0.92;

    /// <summary>最低 10%（与设置页 10 档滑块一致）。</summary>
    public const double MinOpacity = 0.10;

    public const double MaxOpacity = 1.0;

    /// <summary>整窗不透明度（连文字一起），默认 100%。</summary>
    public const double DefaultWindowOpacity = 1.0;

    /// <summary>最低 10%（与设置页 10 档滑块一致）。</summary>
    public const double MinWindowOpacity = 0.10;

    private readonly ITodoRepository _repository;

    /// <summary>
    /// 设置的进程内缓存：设置读得比写得勤（桌面时钟每 5 秒就要比对一轮默认城市），
    /// 不能每读一个属性都全表 SELECT 一遍。写操作走 <see cref="SetSetting"/> 同步更新缓存。
    /// 库里这一张表的写入方只有本服务，所以缓存不会与数据库脱节。
    /// </summary>
    private readonly object _settingsLock = new();
    private Dictionary<string, string>? _settingsCache;

    public AppSettingsService(ITodoRepository repository)
    {
        _repository = repository;
    }

    public static string BoardTopmostKey(string boardId) => $"board.{boardId}.topmost";

    public static string BoardLockedKey(string boardId) => $"board.{boardId}.locked";

    /// <summary>开机自启（HKCU Run 项）。</summary>
    public const string AutoStartKey = "startup.autoStart";

    /// <summary>上次打开着的看板 id 列表（逗号分隔）。</summary>
    public const string OpenBoardsKey = "session.openBoards";

    /// <summary>启动时是否恢复上次会话。</summary>
    public const string RestoreSessionKey = "session.restore";

    /// <summary>开机自启时把看板/时钟沉到桌面底层（不压着别的窗口）。</summary>
    public const string StartupDesktopLevelKey = "startup.desktopLevel";


    /// <summary>Token 数据源（opencode 数据库）的自定义路径，空=默认位置。</summary>
    public const string TokenDatabasePathKey = "token.databasePath";

    /// <summary>Token 面板自动刷新间隔（秒），只允许 5 / 15 / 30。</summary>
    public const string TokenRefreshSecondsKey = "token.refreshSeconds";

    /// <summary>Token 面板上次选的统计范围（天）。</summary>
    public const string TokenRangeDaysKey = "token.rangeDays";

    /// <summary>番茄钟今日完成个数。</summary>
    public const string PomodoroCountTodayKey = "pomodoro.count";

    /// <summary>天气看板的城市列表（逗号分隔，第一个为默认城市）。</summary>
    public const string WeatherCitiesKey = "weather.cities";

    /// <summary>番茄钟计数对应的日期（用来跨天清零）。</summary>
    public const string PomodoroCountDateKey = "pomodoro.countDate";

    /// <summary>
    /// 界面语言（存储键如 zh-CN / en / ja / ko）。无法识别时回落简体中文。
    /// </summary>
    public string Language
    {
        get => Localization.AppLanguages.Resolve(GetString(LanguageKey, null)).Key;
        set => SetSetting(LanguageKey, Localization.AppLanguages.Resolve(value).Key);
    }

    /// <summary>背景色方案。</summary>
    public string Skin
    {
        get => GetString(SkinKey, DefaultSkin);
        set => SetSetting(SkinKey, value);
    }

    /// <summary>背景不透明度（只作用于背景层，文字始终保持清晰）。</summary>
    public double Opacity
    {
        get
        {
            var raw = GetString(OpacityKey, null);
            if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return DefaultOpacity;
            }

            return ClampOpacity(value);
        }
        set => SetSetting(
            OpacityKey,
            ClampOpacity(value).ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>整窗不透明度：连文字一起变淡，更像贴在屏幕上的便签。</summary>
    public double WindowOpacity
    {
        get
        {
            var raw = GetString(WindowOpacityKey, null);
            if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return DefaultWindowOpacity;
            }

            return ClampWindowOpacity(value);
        }
        set => SetSetting(
            WindowOpacityKey,
            ClampWindowOpacity(value).ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>上次在看板上选中的标签胶囊：返回 "all" / "none" / 标签 id；没记录返回 null。</summary>
    public static string BoardChipKey(string boardId) => $"board.{boardId}.chip";
    public string? GetBoardChip(string boardId)
    {
        var raw = GetString(BoardChipKey(boardId), null);
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    public void SetBoardChip(string boardId, string chipKey) =>
        SetSetting(BoardChipKey(boardId), chipKey);

    /// <summary>看板上次用的是列表还是四象限视图。</summary>
    public static string BoardViewKey(string boardId) => $"board.{boardId}.view";

    public string GetBoardView(string boardId) => GetString(BoardViewKey(boardId), "list");

    public void SetBoardView(string boardId, string view) =>
        SetSetting(BoardViewKey(boardId), view);

    // ---------------- 每个窗口自己的透明度 ----------------
    /// <summary>某个窗口单独的整窗透明度存在这个 key 下。</summary>
    public static string WindowOpacityKeyFor(string windowKey) => $"window.{windowKey}.opacity";

    /// <summary>
    /// 取某个窗口的整窗透明度：单独设过就用它自己的，否则跟随全局设置。
    /// <paramref name="windowKey"/> 用看板 id，工具窗用 <c>tool.weather</c> 这类固定 key。
    /// </summary>
    public double GetWindowOpacity(string windowKey)
    {
        var raw = GetString(WindowOpacityKeyFor(windowKey), null);
        if (string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return WindowOpacity;
        }

        return ClampWindowOpacity(value);
    }

    /// <summary>这个窗口有没有单独设过透明度（false = 跟随全局）。</summary>
    public bool HasWindowOpacity(string windowKey) =>
        !string.IsNullOrWhiteSpace(GetString(WindowOpacityKeyFor(windowKey), null));

    public void SetWindowOpacity(string windowKey, double value) =>
        SetSetting(
            WindowOpacityKeyFor(windowKey),
            ClampWindowOpacity(value).ToString("0.###", CultureInfo.InvariantCulture));

    /// <summary>清除单独设置，回到跟随全局。</summary>
    public void ClearWindowOpacity(string windowKey) =>
        SetSetting(WindowOpacityKeyFor(windowKey), string.Empty);

    /// <summary>便签默认置顶，符合“贴纸感”。</summary>
    public bool GetBoardTopmost(string boardId) => GetBool(BoardTopmostKey(boardId), true);
    public void SetBoardTopmost(string boardId, bool value) =>
        SetSetting(BoardTopmostKey(boardId), value ? "1" : "0");

    public bool GetBoardLocked(string boardId) => GetBool(BoardLockedKey(boardId), false);

    public void SetBoardLocked(string boardId, bool value) =>
        SetSetting(BoardLockedKey(boardId), value ? "1" : "0");

    /// <summary>是否开机自启（这里只存"用户期望值"，注册表由 App 层同步）。</summary>
    public bool AutoStart
    {
        get => GetBool(AutoStartKey, false);
        set => SetSetting(AutoStartKey, value ? "1" : "0");
    }

    /// <summary>启动时是否恢复上次打开的看板，默认开。</summary>
    public bool RestoreSession
    {
        get => GetBool(RestoreSessionKey, true);
        set => SetSetting(RestoreSessionKey, value ? "1" : "0");
    }

    /// <summary>
    /// 开机自启时把恢复出来的看板/时钟沉到桌面底层（默认开）：
    /// 开机后它们不压着别的窗口，也不抢焦点；手动启动时保持原来的置顶设置。
    /// </summary>
    public bool StartupDesktopLevel
    {
        get => GetBool(StartupDesktopLevelKey, true);
        set => SetSetting(StartupDesktopLevelKey, value ? "1" : "0");
    }

    /// <summary>
    /// Token 看板要读的 opencode 数据库路径。空表示用默认位置（~/.local/share/opencode/opencode.db）。
    /// </summary>
    public string TokenDatabasePath
    {
        get
        {
            var raw = GetString(TokenDatabasePathKey, null);
            return string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim();
        }
        set => SetSetting(TokenDatabasePathKey, value.Trim());
    }

    /// <summary>Token 面板自动刷新间隔（秒）。只允许 5 / 15 / 30，其它值归一到 15。</summary>
    public int TokenRefreshSeconds
    {
        get
        {
            var raw = GetString(TokenRefreshSecondsKey, null);
            if (!int.TryParse(raw, out var seconds))
            {
                return 15;
            }

            return seconds is 5 or 15 or 30 ? seconds : 15;
        }
        set => SetSetting(TokenRefreshSecondsKey, (value is 5 or 15 or 30 ? value : 15).ToString());
    }

    /// <summary>Token 面板上次选的统计范围（天）：0=今天，3/7/30。</summary>
    public int TokenRangeDays
    {
        get
        {
            var raw = GetString(TokenRangeDaysKey, null);
            if (!int.TryParse(raw, out var days))
            {
                return 0;
            }

            return days is 0 or 3 or 7 or 30 ? days : 0;
        }
        set => SetSetting(TokenRangeDaysKey, (value is 0 or 3 or 7 or 30 ? value : 0).ToString());
    }

    /// <summary>Token 面板上次选的客户端（agent id），空 = 全部。</summary>
    public const string TokenAgentKey = "token.agent";

    public string TokenAgent
    {
        get => (GetString(TokenAgentKey, null) ?? string.Empty).Trim();
        set => SetSetting(TokenAgentKey, value.Trim());
    }

    /// <summary>番茄钟今天完成几个（跨天自动归零）。</summary>
    public int PomodoroCountToday
    {
        get
        {
            var raw = GetString(PomodoroCountTodayKey, null);
            return int.TryParse(raw, out var count) && count > 0 ? count : 0;
        }
        set => SetSetting(PomodoroCountTodayKey, Math.Max(0, value).ToString());
    }

    /// <summary>番茄钟计数对应的日期。</summary>
    public DateOnly? PomodoroCountDate
    {
        get
        {
            var raw = GetString(PomodoroCountDateKey, null);
            return DateOnly.TryParse(raw, out var date) ? date : null;
        }
        set => SetSetting(PomodoroCountDateKey, value?.ToString("yyyy-MM-dd") ?? string.Empty);
    }

    /// <summary>上次开着哪些看板。用逗号拼接，看板数量很少，不需要建表。</summary>
    public IReadOnlyList<string> OpenBoards
    {
        get
        {
            var raw = GetString(OpenBoardsKey, null);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        set => SetSetting(OpenBoardsKey, string.Join(",", value));
    }

    /// <summary>天气看板的城市列表（中文城市名）。默认【双峰、岳麓、芙蓉、苏州、炎陵、杭州】。</summary>
    public IReadOnlyList<string> WeatherCities
    {
        get
        {
            var raw = GetString(WeatherCitiesKey, null);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return DefaultWeatherCities;
            }

            var cities = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return cities.Length > 0 ? cities : DefaultWeatherCities;
        }
        set => SetSetting(WeatherCitiesKey, string.Join(",", value));
    }

    public const string WeatherDefaultCityKey = "weather.defaultCity";

    // ---------------- 护眼（防蓝光滤镜） ----------------

    /// <summary>防蓝光滤镜开关。</summary>
    public const string EyeFilterEnabledKey = "eye.filter.enabled";

    /// <summary>滤镜颜色（#RRGGBB）。预设四档和自定义共用这一个键。</summary>
    public const string EyeFilterColorKey = "eye.filter.color";

    /// <summary>滤镜强度（10-50，步进 5）。</summary>
    public const string EyeFilterStrengthKey = "eye.filter.strength";

    public const string DefaultEyeFilterColor = "#FF9F45";

    public const int DefaultEyeFilterStrength = 25;

    /// <summary>
    /// 豆沙绿护眼（显卡 gamma 校色）：把屏幕上的纯白映射成 #C7EDCC。
    /// 记事本 / Word 这类白底程序的背景跟着变绿，系统级的，不是本应用的窗口。
    /// </summary>
    public const string EyeGammaEnabledKey = "eye.gamma.enabled";

    /// <summary>
    /// 色温护眼：按开尔文色温压 R/G/B 三个通道，
    /// 只改颜色不动对比度，所以没有「蒙一层灰」的雾感。
    /// </summary>
    public const string EyeTempEnabledKey = "eye.temp.enabled";

    public const string EyeTempKelvinKey = "eye.temp.kelvin";

    /// <summary>
    /// 色温改用 DWM 合成层的乘法色矩阵（默认 false：走显卡 gamma，截图/录屏不带色偏）。
    /// 有些环境（虚拟显示器、远程桌面、HDR）会「收下 gamma 表但不生效」，那种情况才需要这个。
    /// </summary>
    public const string EyeTempCompositorKey = "eye.temp.compositor";

    /// <summary>默认色温：3400K（日落档）。</summary>
    public const int DefaultEyeTempKelvin = 3400;

    public const int MinEyeTempKelvin = 1900;
    public const int MaxEyeTempKelvin = 6500;

    public bool EyeTempEnabled
    {
        get => GetBool(EyeTempEnabledKey, false);
        set => SetSetting(EyeTempEnabledKey, value ? "1" : "0");
    }

    public int EyeTempKelvin
    {
        get
        {
            var raw = GetString(EyeTempKelvinKey, null);
            return int.TryParse(raw, out var value)
                ? Math.Clamp(value, MinEyeTempKelvin, MaxEyeTempKelvin)
                : DefaultEyeTempKelvin;
        }
        set => SetSetting(EyeTempKelvinKey, Math.Clamp(value, MinEyeTempKelvin, MaxEyeTempKelvin).ToString());
    }

    public bool EyeTempCompositor
    {
        get => GetBool(EyeTempCompositorKey, false);
        set => SetSetting(EyeTempCompositorKey, value ? "1" : "0");
    }

    public bool EyeGammaEnabled
    {
        get => GetBool(EyeGammaEnabledKey, false);
        set => SetSetting(EyeGammaEnabledKey, value ? "1" : "0");
    }

    public bool EyeFilterEnabled
    {
        get => GetBool(EyeFilterEnabledKey, false);
        set => SetSetting(EyeFilterEnabledKey, value ? "1" : "0");
    }

    public string EyeFilterColor
    {
        get
        {
            var raw = GetString(EyeFilterColorKey, null);
            return string.IsNullOrWhiteSpace(raw) ? DefaultEyeFilterColor : raw.Trim();
        }
        set => SetSetting(EyeFilterColorKey, value.Trim());
    }

    public int EyeFilterStrength
    {
        get
        {
            var raw = GetString(EyeFilterStrengthKey, null);
            return int.TryParse(raw, out var value)
                ? (int)Math.Clamp(Math.Round(value / 5d) * 5, 10, 50)
                : DefaultEyeFilterStrength;
        }
        set => SetSetting(EyeFilterStrengthKey, ((int)Math.Clamp(Math.Round(value / 5d) * 5, 10, 50)).ToString());
    }

    /// <summary>
    /// 默认城市：桌面时钟优先显示它，天气看板打开时也默认选中它。
    /// null = 用城市列表的第一个；设过的城市被删掉后自动作废（返回 null）。
    /// </summary>
    public string? WeatherDefaultCity
    {
        get
        {
            var raw = GetString(WeatherDefaultCityKey, null);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var clean = raw.Trim();
            return WeatherCities.Contains(clean, StringComparer.Ordinal) ? clean : null;
        }
        set => SetSetting(WeatherDefaultCityKey, value?.Trim() ?? string.Empty);
    }

    /// <summary>实际生效的默认城市：没设置（或已失效）就是列表第一个。</summary>
    public string? EffectiveWeatherDefaultCity =>
        WeatherDefaultCity ?? WeatherCities.FirstOrDefault();

    /// <summary>
    /// 默认城市列表（与参考 UI 一致）。
    /// 这些名字都收录在天气模块的内置坐标表里，所以即便 Open-Meteo 的中文地理编码
    /// 查不到（「岳麓」「炎陵」）或会命中同名异地（「双峰」在浙江），也能定位到正确城市。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultWeatherCities =
    [
        "双峰", "岳麓", "芙蓉", "苏州", "炎陵", "杭州",
    ];

    public static double ClampOpacity(double value)
    {
        if (double.IsNaN(value))
        {
            return DefaultOpacity;
        }

        return Math.Min(MaxOpacity, Math.Max(MinOpacity, value));
    }

    public static double ClampWindowOpacity(double value)
    {
        if (double.IsNaN(value))
        {
            return DefaultWindowOpacity;
        }

        return Math.Min(MaxOpacity, Math.Max(MinWindowOpacity, value));
    }

    private string GetString(string key, string? fallback)
    {
        lock (_settingsLock)
        {
            var cache = _settingsCache ??= new Dictionary<string, string>(_repository.GetSettings(), StringComparer.Ordinal);
            return cache.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback ?? string.Empty;
        }
    }

    /// <summary>写库并同步缓存。设成空串表示「清掉该项」，缓存里也留着这个空值，语义一致。</summary>
    private void SetSetting(string key, string value)
    {
        _repository.SetSetting(key, value);
        lock (_settingsLock)
        {
            if (_settingsCache is not null)
            {
                _settingsCache[key] = value;
            }
        }
    }

    private bool GetBool(string key, bool fallback)
    {
        var value = GetString(key, null);
        return string.IsNullOrEmpty(value) ? fallback : value == "1";
    }
}