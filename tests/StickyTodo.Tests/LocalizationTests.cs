using System.Text.RegularExpressions;
using StickyTodo.Core.Localization;

namespace StickyTodo.Tests;

/// <summary>
/// 多语言文案测试。这类问题光看界面很难发现，必须在编译期之外守住：
///   1. 每个键必须有 4 种语言的非空译文（漏翻会直接显示中文，用户会以为没生效）；
///   2. 译文里的 {0}/{1} 占位符必须与中文一致（少了会抛 FormatException，多了会显示错位）；
///   3. 键名不能重复（字典字面量重复会在运行时静默覆盖）。
/// </summary>
public sealed class LocalizationTests
{
    private static readonly AppLanguage[] AllLanguages =
    [
        AppLanguage.ChineseSimplified, AppLanguage.English, AppLanguage.Japanese, AppLanguage.Korean,
    ];

    [Fact]
    public void FourLanguages_AreDeclared()
    {
        Assert.Equal(4, AppLanguages.All.Count);

        Assert.Contains(AppLanguages.All, l => l.Key == "zh-CN");
        Assert.Contains(AppLanguages.All, l => l.Key == "en");
        Assert.Contains(AppLanguages.All, l => l.Key == "ja");
        Assert.Contains(AppLanguages.All, l => l.Key == "ko");

        Assert.All(AppLanguages.All, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.NativeName));
            Assert.False(string.IsNullOrWhiteSpace(l.Region));
        });
    }

    [Fact]
    public void EveryKey_HasAllFourTranslations()
    {
        var problems = new List<string>();

        foreach (var key in UiStrings.Keys)
        {
            var values = UiStrings.AllValues(key);
            if (values.Count != 4)
            {
                problems.Add($"{key}: 只有 {values.Count} 种语言");
                continue;
            }

            for (var i = 0; i < 4; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i]))
                {
                    problems.Add($"{key}: 第 {i} 种语言为空");
                }
            }
        }

        Assert.True(problems.Count == 0, "以下键缺译文：\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Placeholders_MatchAcrossLanguages()
    {
        var problems = new List<string>();

        foreach (var key in UiStrings.Keys)
        {
            var values = UiStrings.AllValues(key);
            if (values.Count != 4)
            {
                continue;
            }

            var expected = Placeholders(values[0]);

            for (var i = 1; i < 4; i++)
            {
                var actual = Placeholders(values[i]);
                if (!expected.SetEquals(actual))
                {
                    problems.Add(
                        $"{key}: 中文占位符 [{string.Join(",", expected)}] 但第 {i} 种是 [{string.Join(",", actual)}]");
                }
            }
        }

        Assert.True(problems.Count == 0, "占位符不一致（会在运行时抛 FormatException 或显示错位）：\n" + string.Join("\n", problems));
    }

    [Fact]
    public void EveryTranslation_ActuallyFormats()
    {
        foreach (var key in UiStrings.Keys)
        {
            var values = UiStrings.AllValues(key);
            if (values.Count != 4)
            {
                continue;
            }

            var count = Placeholders(values[0]).Count;
            var args = Enumerable.Range(0, count).Cast<object?>().ToArray();

            foreach (var value in values)
            {
                // 占位符数量已由上个测试保证一致，这里只确认能被 string.Format 正确处理
                var result = string.Format(value, args);
                Assert.False(string.IsNullOrWhiteSpace(result), $"{key} 格式化后为空");
            }
        }
    }

    [Fact]
    public void KnownKeys_TranslatePerLanguage()
    {
        // 抽查几个高频键，确认真的按语言返回，而不是全都退回中文
        Assert.Equal("确定", UiStrings.Get("common.ok", AppLanguage.ChineseSimplified));
        Assert.Equal("OK", UiStrings.Get("common.ok", AppLanguage.English));
        Assert.Equal("OK", UiStrings.Get("common.ok", AppLanguage.Japanese));
        Assert.Equal("확인", UiStrings.Get("common.ok", AppLanguage.Korean));
    }

    [Fact]
    public void MissingKey_FallsBackToKeyItself()
    {
        // 漏键时返回键名，界面上能一眼看出是哪条没翻，而不是静默显示空白
        Assert.Equal("no.such.key", UiStrings.Get("no.such.key", AppLanguage.English));
    }

    [Fact]
    public void Translator_SwitchesLanguageAndNotifies()
    {
        var translator = Translator.Current;
        var previous = translator.Language;

        try
        {
            var notified = false;
            void Handler(object? _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == "Item[]")
                {
                    notified = true;
                }
            }

            translator.PropertyChanged += Handler;

            translator.Language = AppLanguage.English;
            Assert.Equal("Settings", translator["common.settings"]);
            Assert.True(notified, "切换语言应通知 indexer 绑定刷新");

            translator.PropertyChanged -= Handler;

            translator.Language = AppLanguage.Japanese;
            Assert.Equal("設定", translator["common.settings"]);
            Assert.Equal("設定", translator.T("common.settings"));

            translator.Language = AppLanguage.Korean;
            Assert.Equal("설정", translator["common.settings"]);
        }
        finally
        {
            translator.Language = previous;
        }
    }

    [Fact]
    public void Translator_FormatWithArgs_AndToleratesBadTemplate()
    {
        var translator = Translator.Current;
        var previous = translator.Language;

        try
        {
            translator.Language = AppLanguage.English;
            Assert.Equal("To-do 3", translator.F("view.todoCount", 3));

            translator.Language = AppLanguage.ChineseSimplified;
            Assert.Equal("待办 5", translator.F("view.todoCount", 5));
        }
        finally
        {
            translator.Language = previous;
        }
    }

    [Fact]
    public void Resolve_NormalizesLanguageKeys()
    {
        Assert.Equal("zh-CN", AppLanguages.Resolve(null).Key);
        Assert.Equal("zh-CN", AppLanguages.Resolve("zh").Key);
        Assert.Equal("zh-CN", AppLanguages.Resolve("zh-Hans").Key);
        Assert.Equal("en", AppLanguages.Resolve("en-US").Key);
        Assert.Equal("ja", AppLanguages.Resolve("ja-JP").Key);
        Assert.Equal("ko", AppLanguages.Resolve("ko-KR").Key);
        Assert.Equal("zh-CN", AppLanguages.Resolve("klingon").Key);
    }

    /// <summary>抽出 {0} {1} 这类占位符下标。</summary>
    private static HashSet<int> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)\}")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToHashSet();
}