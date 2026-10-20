using System.Globalization;
using StickyTodo.Core.Abstractions;

namespace StickyTodo.Core.Services;

/// <summary>
/// 外观与窗口偏好。存在通用的 setting 键值表里，加开关不需要改表结构。
/// </summary>
public sealed class AppSettingsService
{
    public const string SkinKey = "appearance.skin";
    public const string OpacityKey = "appearance.opacity";
    public const string WindowOpacityKey = "appearance.windowOpacity";

    public const string DefaultSkin = "sticky-yellow";
    public const double DefaultOpacity = 0.92;
    public const double MinOpacity = 0.35;
    public const double MaxOpacity = 1.0;

    /// <summary>整窗不透明度（连文字一起），默认 100%。</summary>
    public const double DefaultWindowOpacity = 1.0;
    public const double MinWindowOpacity = 0.4;

    private readonly ITodoRepository _repository;

    public AppSettingsService(ITodoRepository repository)
    {
        _repository = repository;
    }

    public static string BoardTopmostKey(string boardId) => $"board.{boardId}.topmost";

    public static string BoardLockedKey(string boardId) => $"board.{boardId}.locked";

    /// <summary>背景色方案。</summary>
    public string Skin
    {
        get => GetString(SkinKey, DefaultSkin);
        set => _repository.SetSetting(SkinKey, value);
    }

    /// <summary>背景不透明度（只作用于背景层，文字始终保持清晰）。</summary>
    public double Opacity
    {
        get
        {
            var raw = GetString(OpacityKey, null);
            if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return DefaultOpacity;
            }

            return ClampOpacity(value);
        }
        set => _repository.SetSetting(
            OpacityKey,
            ClampOpacity(value).ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>整窗不透明度：连文字一起变淡，更像贴在屏幕上的便签。</summary>
    public double WindowOpacity
    {
        get
        {
            var raw = GetString(WindowOpacityKey, null);
            if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return DefaultWindowOpacity;
            }

            return ClampWindowOpacity(value);
        }
        set => _repository.SetSetting(
            WindowOpacityKey,
            ClampWindowOpacity(value).ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>便签默认置顶，符合“贴纸感”。</summary>
    public bool GetBoardTopmost(string boardId) => GetBool(BoardTopmostKey(boardId), true);

    public void SetBoardTopmost(string boardId, bool value) =>
        _repository.SetSetting(BoardTopmostKey(boardId), value ? "1" : "0");

    public bool GetBoardLocked(string boardId) => GetBool(BoardLockedKey(boardId), false);

    public void SetBoardLocked(string boardId, bool value) =>
        _repository.SetSetting(BoardLockedKey(boardId), value ? "1" : "0");

    public static double ClampOpacity(double value)
    {
        if (double.IsNaN(value))
        {
            return DefaultOpacity;
        }

        return Math.Min(MaxOpacity, Math.Max(MinOpacity, value));
    }

    public static double ClampWindowOpacity(double value)
    {
        if (double.IsNaN(value))
        {
            return DefaultWindowOpacity;
        }

        return Math.Min(MaxOpacity, Math.Max(MinWindowOpacity, value));
    }

    private string GetString(string key, string? fallback)
    {
        var all = _repository.GetSettings();
        return all.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback ?? string.Empty;
    }

    private bool GetBool(string key, bool fallback)
    {
        var value = GetString(key, null);
        return string.IsNullOrEmpty(value) ? fallback : value == "1";
    }
}