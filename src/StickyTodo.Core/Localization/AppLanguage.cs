namespace StickyTodo.Core.Localization;

/// <summary>界面语言。数值只用于稳定排序，不要依赖具体值。</summary>
public enum AppLanguage
{
    /// <summary>简体中文（默认）。</summary>
    ChineseSimplified = 0,

    /// <summary>English.</summary>
    English = 1,

    /// <summary>日本語。</summary>
    Japanese = 2,

    /// <summary>한국어。</summary>
    Korean = 3,
}

/// <summary>语言元信息：存储键、本地名称、区域代码。</summary>
public sealed record LanguageInfo(AppLanguage Language, string Key, string NativeName, string Region)
{
    public override string ToString() => NativeName;
}

/// <summary>内置语言列表。</summary>
public static class AppLanguages
{
    public const string DefaultKey = "zh-CN";

    public static readonly LanguageInfo ChineseSimplified =
        new(AppLanguage.ChineseSimplified, "zh-CN", "简体中文", "zh-CN");

    public static readonly LanguageInfo English =
        new(AppLanguage.English, "en", "English", "en");

    public static readonly LanguageInfo Japanese =
        new(AppLanguage.Japanese, "ja", "日本語", "ja-JP");

    public static readonly LanguageInfo Korean =
        new(AppLanguage.Korean, "ko", "한국어", "ko-KR");

    public static readonly IReadOnlyList<LanguageInfo> All =
    [
        ChineseSimplified, English, Japanese, Korean,
    ];

    public static LanguageInfo Resolve(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var trimmed = key.Trim();

            // 先按精确键匹配，再按语言前缀兜底（zh、zh-Hans、en-US 这类都认）
            var exact = All.FirstOrDefault(l => string.Equals(l.Key, trimmed, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var prefix = trimmed.Split('-', '_')[0];
            var byPrefix = All.FirstOrDefault(l =>
                l.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(l.Region.Split('-')[0], prefix, StringComparison.OrdinalIgnoreCase));

            if (byPrefix is not null)
            {
                return byPrefix;
            }
        }

        return ChineseSimplified;
    }
}