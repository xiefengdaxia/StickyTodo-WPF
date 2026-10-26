using System.ComponentModel;
using System.Globalization;

namespace StickyTodo.Core.Localization;

/// <summary>
/// 运行时翻译器（单例）。三种用法：
///   1. C# 里：<c>Loc.T("common.ok")</c> / <c>Loc.F("view.todoCount", 3)</c>
///   2. XAML 里静态文本：<c>Text="{Binding Source={x:Static loc:Loc.Current}, Path=[chrome.lock]}"</c>
///   3. 需要格式化参数时用值转换器：<c>Converter={StaticResource LocFormat}, ConverterParameter=token.total</c>
///
/// 切换语言时触发 <see cref="PropertyChanged"/>（Indexer 名为 <c>Item[]</c>），
/// 所有绑定到 <c>[key]</c> 的文本会自动刷新，不需要重建窗口。
/// </summary>
public sealed class Translator : INotifyPropertyChanged
{
    private AppLanguage _language = AppLanguage.ChineseSimplified;

    public static Translator Current { get; } = new();

    private Translator()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>语言变化（供需要重建内容的代码订阅）。</summary>
    public event EventHandler? LanguageChanged;

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value)
            {
                return;
            }

            _language = value;

            // WPF 的索引器绑定只认 "Item[]"；
            // Avalonia 的索引器绑定只认"所有属性都变了"的空名通知（实测）。
            // 两个都发，两边都能刷新，互不影响。
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public LanguageInfo Info => AppLanguages.Resolve(KeyOf(_language));

    public string KeyOf(AppLanguage language) =>
        AppLanguages.All.FirstOrDefault(l => l.Language == language)?.Key ?? AppLanguages.DefaultKey;

    /// <summary>索引器：XAML 里 <c>Path=[key]</c> 走这里。</summary>
    public string this[string key] => UiStrings.Get(key, _language);

    /// <summary>取文案（不格式化）。</summary>
    public string T(string key) => UiStrings.Get(key, _language);

    /// <summary>取文案并格式化。</summary>
    public string F(string key, params object?[] args)
    {
        var template = UiStrings.Get(key, _language);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // 译文占位符写错时不要把界面搞崩，退回模板原文
            return template;
        }
    }

    /// <summary>当前语言的 .NET CultureInfo（用于日期/数字格式）。</summary>
    public CultureInfo Culture => _language switch
    {
        AppLanguage.English => new CultureInfo("en-US"),
        AppLanguage.Japanese => new CultureInfo("ja-JP"),
        AppLanguage.Korean => new CultureInfo("ko-KR"),
        _ => new CultureInfo("zh-CN"),
    };
}

/// <summary>给 C# 代码用的简写。</summary>
public static class Loc
{
    public static Translator Current => Translator.Current;

    public static AppLanguage Language => Translator.Current.Language;

    public static string T(string key) => Translator.Current.T(key);

    public static string F(string key, params object?[] args) => Translator.Current.F(key, args);
}