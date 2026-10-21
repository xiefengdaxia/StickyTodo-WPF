using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Services;

/// <summary>
/// 语言切换的实际执行者。
///
/// WPF 的一个坑：<c>DynamicResource</c> 会跟着资源变，但 `<c>Binding Converter=...</c>`
/// 和普通字符串属性**不会**因为语言变化自动重跑。所以切语言后必须：
///   1. 改 <see cref="Translator.Language"/>（让 indexer 绑定通过 PropertyChanged 刷新）；
///   2. 遍历所有窗口的可视树，把所有带转换器的绑定重新 UpdateTarget。
/// 只做第 1 步的话，界面要重启程序才会变。
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
        Translator.Current.Language = info.Language;

        RefreshAllWindows();
    }

    /// <summary>
    /// 让所有窗口重新求值受语言影响的绑定。
    /// 遍历可视树找 <see cref="FrameworkElement"/>，用 <c>LocalValueEnumerator</c>
    /// 拿到本元素上设置的绑定并逐个 UpdateTarget。
    /// </summary>
    public static void RefreshAllWindows()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            // ViewModel 里由 C# 拼出来的文案（视图计数、状态行、标签名）
            // 不会被 XAML 绑定自动刷新，需要显式通知。
            if (window is Views.BoardWindow { DataContext: ViewModels.BoardViewModel boardVm })
            {
                boardVm.RefreshLocalizedText();
            }
            else if (window is Views.ManagerWindow { DataContext: ViewModels.ManagerViewModel managerVm })
            {
                managerVm.RefreshLocalizedText();
            }
            else if (window is Views.PomodoroWindow { ViewModel: { } pomodoroVm })
            {
                pomodoroVm.Sync();
            }

            RefreshElement(window);
        }
    }

    private static void RefreshElement(DependencyObject root)
    {
        UpdateBindings(root);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            RefreshElement(VisualTreeHelper.GetChild(root, i));
        }
    }

    private static void UpdateBindings(DependencyObject element)
    {
        var enumerator = element.GetLocalValueEnumerator();

        while (enumerator.MoveNext())
        {
            var entry = enumerator.Current;
            if (entry.Value is not BindingExpression expression)
            {
                continue;
            }

            try
            {
                expression.UpdateTarget();
            }
            catch (InvalidOperationException)
            {
                // 元素还没进入可视树/已被回收，忽略即可
            }
        }
    }
}