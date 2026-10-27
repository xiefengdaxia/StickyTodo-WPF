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
        set => _repository.SetSetting(LanguageKey, Localization.AppLanguages.Resolve(value).Key);
    }

    /// <summary>背景色方案。</summary>
    public string Skin
    {
        get => GetString(SkinKey, DefaultSkin);
        set => _repository.SetSetting(SkinKey, value);
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
        set => _repository.SetSetting(
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
        set => _repository.SetSetting(
            WindowOpacityKey,
            ClampWindowOpacity(value).ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>便签默认置顶，符合“贴纸感”。</summary>
    public bool GetBoardTopmost(string boardId) => GetBool(BoardTopmostKey(boardId), true);

    public void SetBoardTopmost(string boardId, bool value) =>
        _repository.SetSetting(BoardTopmostKey(boardId), value ? "1" : "0");

    public bool GetBoardLocked(string boardId) => GetBool(BoardLockedKey(boardId), false);

    public void SetBoardLocked(string boardId, bool value) =>
        _repository.SetSetting(BoardLockedKey(boardId), value ? "1" : "0");

    /// <summary>是否开机自启（这里只存"用户期望值"，注册表由 App 层同步）。</summary>
    public bool AutoStart
    {
        get => GetBool(AutoStartKey, false);
        set => _repository.SetSetting(AutoStartKey, value ? "1" : "0");
    }

    /// <summary>启动时是否恢复上次打开的看板，默认开。</summary>
    public bool RestoreSession
    {
        get => GetBool(RestoreSessionKey, true);
        set => _repository.SetSetting(RestoreSessionKey, value ? "1" : "0");
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
        set => _repository.SetSetting(TokenDatabasePathKey, value.Trim());
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
        set => _repository.SetSetting(TokenRefreshSecondsKey, (value is 5 or 15 or 30 ? value : 15).ToString());
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
        set => _repository.SetSetting(TokenRangeDaysKey, (value is 0 or 3 or 7 or 30 ? value : 0).ToString());
    }

    /// <summary>Token 面板上次选的客户端（agent id），空 = 全部。</summary>
    public const string TokenAgentKey = "token.agent";

    public string TokenAgent
    {
        get => (GetString(TokenAgentKey, null) ?? string.Empty).Trim();
        set => _repository.SetSetting(TokenAgentKey, value.Trim());
    }

    /// <summary>番茄钟今天完成几个（跨天自动归零）。</summary>
    public int PomodoroCountToday
    {
        get
        {
            var raw = GetString(PomodoroCountTodayKey, null);
            return int.TryParse(raw, out var count) && count > 0 ? count : 0;
        }
        set => _repository.SetSetting(PomodoroCountTodayKey, Math.Max(0, value).ToString());
    }

    /// <summary>番茄钟计数对应的日期。</summary>
    public DateOnly? PomodoroCountDate
    {
        get
        {
            var raw = GetString(PomodoroCountDateKey, null);
            return DateOnly.TryParse(raw, out var date) ? date : null;
        }
        set => _repository.SetSetting(PomodoroCountDateKey, value?.ToString("yyyy-MM-dd") ?? string.Empty);
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
        set => _repository.SetSetting(OpenBoardsKey, string.Join(",", value));
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
        set => _repository.SetSetting(WeatherCitiesKey, string.Join(",", value));
    }

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
        var all = _repository.GetSettings();
        return all.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback ?? string.Empty;
    }

    private bool GetBool(string key, bool fallback)
    {
        var value = GetString(key, null);
        return string.IsNullOrEmpty(value) ? fallback : value == "1";
    }
}