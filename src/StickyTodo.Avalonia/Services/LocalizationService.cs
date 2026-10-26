using System.Reflection;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 管理面板用的固定字段名（供外部按名字取值，例如自动化脚本）。
/// </summary>
public static class ManagerFields
{
    public const string Boards = "Boards";
}

/// <summary>
/// 语言切换的实际执行者。
///
/// Avalonia 比 WPF 省事：<c>{loc:Loc key}</c> 绑的是 <see cref="Translator"/> 的索引器，
/// 翻译器发出 <c>Item[]</c> 通知后绑定会自动重算，不需要遍历可视树手动 UpdateTarget。
/// 但 ViewModel 里用 C# 拼出来的文案（计数、状态行、标签名）仍要显式通知。
/// </summary>
public sealed class LocalizationService
{
    private readonly AppSettingsService _settings;

    public LocalizationService(AppSettingsService settings)
    {
        _settings = settings;
    }

    public AppLanguage Language => Translator.Current.Language;

    /// <summary>启动时把设置里的语言读进来。</summary>
    public void ApplyFromSettings()
    {
        Translator.Current.Language = AppLanguages.Resolve(_settings.Language).Language;
    }

    /// <summary>切换语言：先落库，再刷新所有已打开的窗口。</summary>
    public void SetLanguage(LanguageInfo info)
    {
        _settings.Language = info.Key;

        // 赋值会触发 Translator 的 Item[] / LanguageChanged 通知，
        // XAML 里的 {loc:Loc} 绑定与订阅了事件的地方会自动跟进。
        Translator.Current.Language = info.Language;
    }

    /// <summary>语言变化。（等价于 WPF 版的 RefreshAllWindows。）</summary>
    public static event EventHandler? LanguageChanged
    {
        add => Translator.Current.LanguageChanged += value;
        remove => Translator.Current.LanguageChanged -= value;
    }
}
