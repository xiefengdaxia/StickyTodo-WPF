using System.Collections.Concurrent;
using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using StickyTodo.Core.Localization;

namespace StickyTodo.Notes.Localization;

/// <summary>
/// 一条界面文案的可绑定包装。
///
/// 为什么不用 <c>{Binding [key], Source=Translator.Current}</c> 的索引器绑定？
/// 实测 Avalonia 的索引器绑定在源对象发出属性变更通知后并不总会重新求值
/// （WPF 与 Avalonia 的通知约定不一致）。这里改成"一个 key 一个对象"，
/// 语言切换时逐个对象发 PropertyChanged，刷新时机完全可控、无歧义。
/// </summary>
public sealed class LocalizedString : INotifyPropertyChanged
{
    private static readonly ConcurrentDictionary<string, LocalizedString> Cache = new();

    private LocalizedString(string key) => Key = key;

    public string Key { get; }

    /// <summary>当前语言下的文案。</summary>
    public string Value => Loc.T(Key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>取（或创建）某个 key 的绑定对象。</summary>
    public static LocalizedString Get(string key) => Cache.GetOrAdd(key, k => new LocalizedString(k));

    /// <summary>语言变了：所有文案对象重新求值。</summary>
    public static void RefreshAll()
    {
        foreach (var item in Cache.Values)
        {
            item.PropertyChanged?.Invoke(item, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    /// <summary>只通知指定 key（用于零散刷新）。</summary>
    public static void Refresh(string key)
    {
        if (Cache.TryGetValue(key, out var item))
        {
            item.PropertyChanged?.Invoke(item, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
}

/// <summary>
/// XAML 里取界面文案：<c>Text="{loc:Loc chrome.lock}"</c>。
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    /// <summary>UiStrings 里的键，例如 <c>chrome.lock</c>。</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizedString.Value))
        {
            Source = LocalizedString.Get(Key),
            Mode = BindingMode.OneWay,
        };
}
