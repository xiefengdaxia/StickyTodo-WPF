using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.App.Services;
using StickyTodo.Core.Entitlements;
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

    private bool _suppressSync;

    public SettingsViewModel(
        SkinService skin,
        AppSettingsService settings,
        TodoService todos,
        IEntitlementService entitlement,
        WindowManager windows)
    {
        _skin = skin;
        _settings = settings;
        _todos = todos;
        _entitlement = entitlement;
        _windows = windows;

        BuildSkinOptions();

        _suppressSync = true;
        _backgroundOpacity = Math.Round(_skin.Opacity * 100);
        _windowOpacity = Math.Round(_skin.WindowOpacity * 100);
        _suppressSync = false;

        RefreshText();
    }

    public ObservableCollection<SkinOptionViewModel> Skins { get; } = new();

    public string EntitlementText =>
        _entitlement.Tier == PlanTier.Lifetime
            ? $"{_entitlement.DisplayName} · 全部功能已解锁"
            : $"{_entitlement.DisplayName} · 部分功能受限";

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

        StatsText = $"{boards} 个看板 · {pending} 条待办 · {done} 条已完成 · {tags} 个标签";
    }
}