using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.App.Services;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Infrastructure;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 独立设置页：外观（皮肤 / 背景透明度 / 整窗透明度）+ 数据 + 快捷键说明。
/// 所有改动即时生效并写入本地数据库。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SkinService _skin;
    private readonly AppSettingsService _settings;
    private readonly TodoService _todos;
    private readonly IEntitlementService _entitlement;
    private readonly WindowManager _windows;
    private readonly AutoStartService _autoStart;
    private readonly LocalizationService _localization;

    private bool _suppressSync;

    public SettingsViewModel(
        SkinService skin,
        AppSettingsService settings,
        TodoService todos,
        IEntitlementService entitlement,
        WindowManager windows,
        AutoStartService autoStart,
        LocalizationService localization)
    {
        _skin = skin;
        _settings = settings;
        _todos = todos;
        _entitlement = entitlement;
        _windows = windows;
        _autoStart = autoStart;
        _localization = localization;

        BuildSkinOptions();
        BuildLanguageOptions();
        LoadStartupSettings();

        _suppressSync = true;
        _backgroundOpacity = Math.Round(_skin.Opacity * 100);
        _windowOpacity = Math.Round(_skin.WindowOpacity * 100);
        _suppressSync = false;

        RefreshText();
    }

    public ObservableCollection<SkinOptionViewModel> Skins { get; } = new();

    /// <summary>可选语言（原生名称，不翻译——用户永远能认出自己的母语）。</summary>
    public ObservableCollection<LanguageInfo> Languages { get; } = new();

    [ObservableProperty]
    private LanguageInfo? _selectedLanguage;

    private bool _suppressLanguageSync;

    private void BuildLanguageOptions()
    {
        _suppressLanguageSync = true;
        try
        {
            Languages.Clear();
            foreach (var info in AppLanguages.All)
            {
                Languages.Add(info);
            }

            SelectedLanguage = AppLanguages.Resolve(_settings.Language);
        }
        finally
        {
            _suppressLanguageSync = false;
        }
    }

    /// <summary>下拉框选中语言：立即落库、立即切换界面文案。</summary>
    partial void OnSelectedLanguageChanged(LanguageInfo? value)
    {
        if (_suppressLanguageSync || value is null)
        {
            return;
        }

        _localization.SetLanguage(value);

        // 语言一变，这里所有由 C# 拼出来的文案都要重算
        RefreshText();
        StatusText = Loc.T("settings.language");
    }

    /// <summary>会员档位的显示名（按当前语言）。Core 里的 DisplayName 是给日志/诊断用的中文常量。</summary>
    private string EntitlementName =>
        Loc.T(_entitlement.Tier == PlanTier.Lifetime ? "entitlement.lifetime" : "entitlement.free");

    public string EntitlementText =>
        _entitlement.Tier == PlanTier.Lifetime
            ? Loc.F("settings.entitlementLifetime", EntitlementName)
            : Loc.F("settings.entitlementLimited", EntitlementName);

    public string DataPathText => AppPaths.DataDirectory;

    public string ShortcutNewTodo => HotKeyService.NewTodoHotKey;

    public string ShortcutToggleBoards => HotKeyService.ToggleBoardsHotKey;

    public string ShortcutLockActive => HotKeyService.LockActiveHotKey;

    /// <summary>背景透明度，0-100 的百分比（滑块绑定）。</summary>
    [ObservableProperty]
    private double _backgroundOpacity;

    /// <summary>整窗透明度，0-100 的百分比（滑块绑定）。</summary>
    [ObservableProperty]
    private double _windowOpacity;

    [ObservableProperty]
    private string _backgroundOpacityText = string.Empty;

    [ObservableProperty]
    private string _windowOpacityText = string.Empty;

    [ObservableProperty]
    private string _statsText = string.Empty;

    /// <summary>开机自启。改这个会立即写注册表。</summary>
    [ObservableProperty]
    private bool _launchAtStartup;

    /// <summary>启动时恢复上次打开的看板。</summary>
    [ObservableProperty]
    private bool _restoreSession = true;

    /// <summary>Token 看板要读的 opencode 数据库路径（空=默认位置）。</summary>
    [ObservableProperty]
    private string _tokenDatabasePath = string.Empty;

    private bool _suppressStartupSync;

    /// <summary>把存的值读进 UI（构造后调用一次）。</summary>
    private void LoadStartupSettings()
    {
        _suppressStartupSync = true;
        try
        {
            // 以注册表实际状态为准：用户可能在别处删过启动项
            LaunchAtStartup = _autoStart.IsEnabled();
            RestoreSession = _settings.RestoreSession;
            TokenDatabasePath = _settings.TokenDatabasePath;
            _settings.AutoStart = LaunchAtStartup;
        }
        finally
        {
            _suppressStartupSync = false;
        }
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_suppressStartupSync)
        {
            return;
        }

        _settings.AutoStart = value;

        if (_autoStart.SetEnabled(value, out var error))
        {
            ErrorText = null;
            StatusText = value ? Loc.T("settings.autoStartOn") : Loc.T("settings.autoStartOff");
        }
        else
        {
            ErrorText = Loc.F("settings.autoStartFailed", error);

            // 写失败就把开关拨回去，避免显示与实际不符
            _suppressStartupSync = true;
            LaunchAtStartup = _autoStart.IsEnabled();
            _suppressStartupSync = false;
        }
    }

    partial void OnRestoreSessionChanged(bool value)
    {
        if (_suppressStartupSync)
        {
            return;
        }

        _settings.RestoreSession = value;
        StatusText = value ? Loc.T("settings.restoreOn") : Loc.T("settings.restoreOff");
    }

    /// <summary>路径失焦/回车时保存；留空表示回到默认位置。</summary>
    public void CommitTokenDatabasePath()
    {
        var text = (TokenDatabasePath ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            _settings.TokenDatabasePath = string.Empty;
            TokenDatabasePath = string.Empty;
            ErrorText = null;
            StatusText = Loc.T("settings.tokenDbReset");
            return;
        }

        if (!System.IO.File.Exists(text))
        {
            ErrorText = Loc.T("settings.tokenDbInvalid");
            TokenDatabasePath = _settings.TokenDatabasePath;
            return;
        }

        _settings.TokenDatabasePath = text;
        ErrorText = null;
        StatusText = Loc.T("settings.tokenDbSaved");
    }

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    private void BuildSkinOptions()
    {
        Skins.Clear();
        foreach (var skin in NoteSkins.All)
        {
            var option = new SkinOptionViewModel(skin)
            {
                IsSelected = string.Equals(skin.Key, _skin.Current.Key, StringComparison.OrdinalIgnoreCase),
            };
            Skins.Add(option);
        }
    }

    public void SelectSkin(SkinOptionViewModel option)
    {
        if (option is null)
        {
            return;
        }

        foreach (var item in Skins)
        {
            item.IsSelected = ReferenceEquals(item, option);
        }

        _skin.SetSkin(option.Skin.Key);
        _windows.ApplySkin();
        RefreshText();
    }

    partial void OnBackgroundOpacityChanged(double value)
    {
        if (_suppressSync)
        {
            return;
        }

        var clamped = Math.Clamp(value, AppSettingsService.MinOpacity * 100, 100);
        _skin.SetOpacity(clamped / 100d);
        _windows.ApplySkin();
        BackgroundOpacityText = $"{Math.Round(clamped)}%";
    }

    partial void OnWindowOpacityChanged(double value)
    {
        if (_suppressSync)
        {
            return;
        }

        var clamped = Math.Clamp(value, AppSettingsService.MinWindowOpacity * 100, 100);
        _skin.SetWindowOpacity(clamped / 100d);
        _windows.ApplySkin();
        WindowOpacityText = $"{Math.Round(clamped)}%";
    }

    public void RefreshText()
    {
        BackgroundOpacityText = $"{Math.Round(BackgroundOpacity)}%";
        WindowOpacityText = $"{Math.Round(WindowOpacity)}%";

        // 这些文案是在 C# 里拼的，语言切换后必须重算，否则页面上会中英混杂
        OnPropertyChanged(nameof(EntitlementText));
        OnPropertyChanged(nameof(StatsText));

        // 通知所有由转换器取文案的绑定重新求值
        LocalizationService.RefreshAllWindows();
    }

    /// <summary>从数据库重新统计，避免设置页显示旧数字。</summary>
    public async Task RefreshStatsAsync()
    {
        var (boards, pending, done, tags) = await Task.Run(() =>
        {
            var all = _todos.GetBoards();
            var todos = all.SelectMany(b => _todos.GetTodos(b.Id)).ToList();
            return (
                all.Count,
                todos.Count(t => !t.IsDone),
                todos.Count(t => t.IsDone),
                _todos.GetTags().Count);
        });

        StatsText = Loc.F("settings.statsLine", boards, pending, done, tags);
    }
}