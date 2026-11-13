using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;
using StickyTodo.Infrastructure;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.ViewModels;

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
    private readonly EyeCareFilter _eyeCare;
    private readonly EyeGammaService _eyeGamma;

    private bool _suppressSync;
    private bool _suppressEyeSync;

    public SettingsViewModel(
        SkinService skin,
        AppSettingsService settings,
        TodoService todos,
        IEntitlementService entitlement,
        WindowManager windows,
        AutoStartService autoStart,
        LocalizationService localization,
        EyeCareFilter eyeCare,
        EyeGammaService eyeGamma)
    {
        _skin = skin;
        _settings = settings;
        _todos = todos;
        _entitlement = entitlement;
        _windows = windows;
        _autoStart = autoStart;
        _localization = localization;
        _eyeCare = eyeCare;
        _eyeGamma = eyeGamma;

        BuildSkinOptions();
        BuildLanguageOptions();
        LoadStartupSettings();
        LoadEyeFilterSettings();

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

    /// <summary>会员档位的显示名。</summary>
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

    /// <summary>开机自启时把看板/时钟沉到桌面底层，不压着别的窗口。</summary>
    [ObservableProperty]
    private bool _startupDesktopLevel = true;

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
            StartupDesktopLevel = _settings.StartupDesktopLevel;
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

    partial void OnStartupDesktopLevelChanged(bool value)
    {
        if (_suppressStartupSync)
        {
            return;
        }

        _settings.StartupDesktopLevel = value;
        StatusText = value ? Loc.T("settings.desktopLevelOn") : Loc.T("settings.desktopLevelOff");
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

        if (!File.Exists(text))
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

    // ---------------- 护眼 ----------------

    /// <summary>豆沙绿护眼（显卡 gamma 校色，屏幕纯白 → #C7EDCC）。</summary>
    [ObservableProperty]
    private bool _eyeGammaEnabled;

    /// <summary>防蓝光滤镜开关。</summary>
    [ObservableProperty]
    private bool _eyeFilterEnabled;

    /// <summary>滤镜强度，10-50 的百分比（滑块绑定）。</summary>
    [ObservableProperty]
    private double _eyeFilterStrength;

    /// <summary>自定义颜色输入框的内容（#RRGGBB，回车/失焦提交）。</summary>
    [ObservableProperty]
    private string _eyeFilterColorText = AppSettingsService.DefaultEyeFilterColor;

    [ObservableProperty]
    private string _eyeStrengthText = string.Empty;

    /// <summary>色温护眼：只按色温压通道，不叠色层。</summary>
    [ObservableProperty]
    private bool _eyeTempEnabled;

    /// <summary>色温滑块（1900-6500K，100K 一档）。</summary>
    [ObservableProperty]
    private double _eyeTempKelvin = AppSettingsService.DefaultEyeTempKelvin;

    [ObservableProperty]
    private string _eyeTempText = string.Empty;

    /// <summary>三种护眼方式共用整块屏幕，同时开只会互相打架，所以互相排斥。</summary>
    private enum EyeMode
    {
        None,
        Temp,
        Gamma,
        Filter,
    }

    /// <summary>关掉除 keep 以外的护眼方式，并同步界面勾选。</summary>
    private void TurnOffOtherEyeModes(EyeMode keep)
    {
        _suppressEyeSync = true;
        try
        {
            if (keep != EyeMode.Temp)
            {
                EyeTempEnabled = false;
                _settings.EyeTempEnabled = false;
            }

            if (keep != EyeMode.Gamma)
            {
                EyeGammaEnabled = false;
                _settings.EyeGammaEnabled = false;
            }

            if (keep != EyeMode.Filter)
            {
                EyeFilterEnabled = false;
                _settings.EyeFilterEnabled = false;
            }
        }
        finally
        {
            _suppressEyeSync = false;
        }
    }

    /// <summary>右对齐显示当前生效的预设名（跟随 EyeFilterColor）。</summary>
    public string EyePresetText
    {
        get
        {
            var color = _settings.EyeFilterColor;
            var preset = EyeCareFilter.PresetList.FirstOrDefault(p =>
                string.Equals(p.Hex, color, StringComparison.OrdinalIgnoreCase));
            return preset.Key is null ? Loc.T("settings.eyeCustomColor") : Loc.T(preset.Key);
        }
    }

    private void LoadEyeFilterSettings()
    {
        _suppressEyeSync = true;
        try
        {
            EyeGammaEnabled = _settings.EyeGammaEnabled;
            EyeFilterEnabled = _settings.EyeFilterEnabled;
            EyeFilterStrength = _settings.EyeFilterStrength;
            EyeFilterColorText = _settings.EyeFilterColor;
            EyeStrengthText = $"{EyeFilterStrength}%";
            EyeTempEnabled = _settings.EyeTempEnabled;
            EyeTempKelvin = _settings.EyeTempKelvin;
            EyeTempText = $"{_settings.EyeTempKelvin}K";
        }
        finally
        {
            _suppressEyeSync = false;
        }
    }

    partial void OnEyeGammaEnabledChanged(bool value)
    {
        if (_suppressEyeSync)
        {
            return;
        }

        if (value)
        {
            TurnOffOtherEyeModes(EyeMode.Gamma);
            _eyeCare.ApplyFromSettings();
        }

        _settings.EyeGammaEnabled = value;
        _eyeGamma.ApplyFromSettings();
        StatusText = value ? Loc.T("settings.eyeGammaOn") : Loc.T("settings.eyeGammaOff");
    }

    partial void OnEyeFilterEnabledChanged(bool value)
    {
        if (_suppressEyeSync)
        {
            return;
        }

        if (value)
        {
            TurnOffOtherEyeModes(EyeMode.Filter);
            _eyeGamma.ApplyFromSettings();
        }

        _settings.EyeFilterEnabled = value;
        _eyeCare.ApplyFromSettings();
        StatusText = value ? Loc.T("settings.eyeFilterOn") : Loc.T("settings.eyeFilterOff");
    }

    partial void OnEyeTempEnabledChanged(bool value)
    {
        if (_suppressEyeSync)
        {
            return;
        }

        if (value)
        {
            TurnOffOtherEyeModes(EyeMode.Temp);
            _eyeCare.ApplyFromSettings();
        }

        _settings.EyeTempEnabled = value;
        _eyeGamma.ApplyFromSettings();
        StatusText = value ? Loc.T("settings.eyeTempOn") : Loc.T("settings.eyeTempOff");
    }

    partial void OnEyeTempKelvinChanged(double value)
    {
        var kelvin = Math.Clamp(
            (int)Math.Round(value),
            AppSettingsService.MinEyeTempKelvin,
            AppSettingsService.MaxEyeTempKelvin);

        EyeTempText = $"{kelvin}K";

        if (_suppressEyeSync)
        {
            return;
        }

        _settings.EyeTempKelvin = kelvin;

        if (_settings.EyeTempEnabled)
        {
            _eyeGamma.ApplyFromSettings();
        }
    }

    partial void OnEyeFilterStrengthChanged(double value)
    {
        EyeStrengthText = $"{Math.Round(value)}%";

        if (_suppressEyeSync)
        {
            return;
        }

        _settings.EyeFilterStrength = (int)Math.Round(value);
        _eyeCare.ApplyFromSettings();
    }

    /// <summary>点了预设色板：写颜色、顺手把滤镜打开。</summary>
    public void SetEyePreset(string hex)
    {
        _settings.EyeFilterColor = hex;
        _settings.EyeFilterEnabled = true;
        TurnOffOtherEyeModes(EyeMode.Filter);
        _eyeGamma.ApplyFromSettings();

        _suppressEyeSync = true;
        try
        {
            EyeFilterColorText = hex;
            EyeFilterEnabled = true;
        }
        finally
        {
            _suppressEyeSync = false;
        }

        OnPropertyChanged(nameof(EyePresetText));
        _eyeCare.ApplyFromSettings();
    }

    /// <summary>自定义颜色提交：只认 #RRGGBB，不合法就拨回原值。</summary>
    public void CommitEyeCustomColor(string? text)
    {
        var clean = (text ?? string.Empty).Trim();

        if (!System.Text.RegularExpressions.Regex.IsMatch(clean, "^#[0-9A-Fa-f]{6}$"))
        {
            EyeFilterColorText = _settings.EyeFilterColor;
            OnPropertyChanged(nameof(EyePresetText));
            return;
        }

        _settings.EyeFilterColor = clean;
        _settings.EyeFilterEnabled = true;
        TurnOffOtherEyeModes(EyeMode.Filter);
        _eyeGamma.ApplyFromSettings();

        _suppressEyeSync = true;
        try
        {
            EyeFilterColorText = clean;
            EyeFilterEnabled = true;
        }
        finally
        {
            _suppressEyeSync = false;
        }

        OnPropertyChanged(nameof(EyePresetText));
        _eyeCare.ApplyFromSettings();
    }

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
