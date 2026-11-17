using System.Collections.Generic;
using System.Windows.Media;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Services;

/// <summary>一个看板图标的呈现方式：字符 + 画它用的字体。</summary>
/// <remarks>
/// 为什么要把字体一起带出来：彩色 emoji（✨🧩📊…）走的是 Segoe UI Emoji，
/// 在 <c>TextFormattingMode="Display"</c> 下只有一个字符宽度的格子，
/// 结果就是「每张图标只露出半个笔画」。单色几何字符必须显式指定
/// Segoe UI Symbol，并且让调用方按 Ideal 排版，字形才完整。
/// </remarks>
public readonly record struct BoardGlyph(string Glyph, FontFamily Font);

/// <summary>图标选择板里的一格：key + 字形 + 名字（名字跟着当前语言变）。</summary>
public sealed class BoardIconPreset
{
    public BoardIconPreset(string key, BoardGlyph glyph)
    {
        Key = key;
        Glyph = glyph;
    }

    /// <summary>存进设置的稳定 key（<c>star</c> / <c>grid</c> …），不随语言和字体变。</summary>
    public string Key { get; }

    public BoardGlyph Glyph { get; }

    public string Text => Glyph.Glyph;

    public FontFamily Font => Glyph.Font;

    /// <summary>ToolTip 里的名字（四语齐全，缺键时退回 key 本身）。</summary>
    public string Name => Loc.T($"icon.{Key}");
}

/// <summary>
/// 看板图标唯一入口：key → 字形 + 字体。管理面板的列表 / 图标网格、
/// 桌面时钟左侧快捷列都走这里，同一块看板在哪儿都画成同一个字形。
/// </summary>
public static class BoardIcons
{
    /// <summary>
    /// 单色符号字体栈：先找 Segoe UI Symbol（几何、杂项符号都在里面），
    /// 缺字再退回 Segoe UI（不落进彩色 emoji 字体，就不会被裁成半个字）。
    /// </summary>
    public const string FontStack = "Segoe UI Symbol, Segoe UI";

    public static readonly FontFamily Font = new(FontStack);

    /// <summary>图标统一字号（选择板和卡片大图标按需要再缩放）。</summary>
    public const double GlyphSize = 18;

    /// <summary>key → 字形。全部是能稳定渲染的单色几何 / 符号字符。</summary>
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        ["star"] = "★",
        ["grid"] = "▦",
        ["chart"] = "▤",
        ["trend"] = "▲",
        ["bulb"] = "✦",
        ["target"] = "◈",
        ["gear"] = "⚙",
        ["palette"] = "✿",
        ["bell"] = "✧",
        ["rocket"] = "◆",
        ["doc"] = "✎",
        ["clock"] = "◷",
        ["heart"] = "♥",
        ["flag"] = "⚑",
        ["book"] = "▥",
        ["music"] = "♪",
        ["sun"] = "☀",
        ["moon"] = "☾",
    };

    /// <summary>
    /// 旧版本存在设置里的 emoji → 现在的 key。
    /// 不做数据迁移（不动库），读的时候折算一下，旧看板不会显示成方块。
    /// </summary>
    private static readonly Dictionary<string, string> LegacyKeys = new(StringComparer.Ordinal)
    {
        ["✨"] = "star",
        ["🧩"] = "grid",
        ["📊"] = "chart",
        ["📈"] = "trend",
        ["🧠"] = "bulb",
        ["🎯"] = "target",
        ["🔧"] = "gear",
        ["🎨"] = "palette",
        ["🔔"] = "bell",
        ["🚀"] = "rocket",
        ["💡"] = "bulb",
        ["🗂"] = "doc",
    };

    /// <summary>图标选择板按这个顺序铺（6 列 × 3 行 = 18 个）。</summary>
    public static IReadOnlyList<BoardIconPreset> Presets { get; } = BuildPresets();

    /// <summary>缺省字形（看板没设过图标、或者存了认不出的值）。</summary>
    public static BoardGlyph Default => ForKey(AppSettingsService.DefaultBoardIcon);

    /// <summary>按 key 取字形；认不出就回落缺省。</summary>
    public static BoardGlyph ForKey(string? key) =>
        key is not null && Glyphs.TryGetValue(key, out var glyph)
            ? new BoardGlyph(glyph, Font)
            : new BoardGlyph(Glyphs[AppSettingsService.DefaultBoardIcon], Font);

    /// <summary>
    /// 设置里读出来的值 → 字形。值可能是新 key，也可能是旧版本存的 emoji；
    /// 两者都认不出就回落缺省，绝不把认不出的字符直接画到界面上。
    /// </summary>
    public static BoardGlyph Resolve(string? storedValue)
    {
        var raw = storedValue?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return Default;
        }

        if (Glyphs.ContainsKey(raw))
        {
            return ForKey(raw);
        }

        return LegacyKeys.TryGetValue(raw, out var key) ? ForKey(key) : Default;
    }

    /// <summary>某类看板的固定图标（AI 看板走 <see cref="Resolve"/>，图标可换）。</summary>
    public static BoardGlyph ForKind(BoardKind kind) => kind switch
    {
        BoardKind.Memo => ForKey("doc"),
        BoardKind.Calendar => ForKey("grid"),
        BoardKind.Token => ForKey("chart"),
        BoardKind.Weather => ForKey("sun"),
        BoardKind.WorldClock => ForKey("moon"),
        BoardKind.DesktopClock => ForKey("clock"),
        BoardKind.NetworkSpeed => ForKey("trend"),
        BoardKind.Performance => ForKey("target"),
        BoardKind.Custom => Default,
        _ => ForKey("flag"),   // 待办看板：⚑ 收件箱
    };

    /// <summary>番茄时钟：不在看板体系里，但桌面时钟列和它放同一排，字形一起统一。</summary>
    public static BoardGlyph Pomodoro => ForKey("bell");

    private static IReadOnlyList<BoardIconPreset> BuildPresets()
    {
        var presets = new List<BoardIconPreset>(AppSettingsService.BoardIconPresets.Count);

        foreach (var key in AppSettingsService.BoardIconPresets)
        {
            presets.Add(new BoardIconPreset(key, ForKey(key)));
        }

        return presets;
    }
}
