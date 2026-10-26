namespace StickyTodo.Notes.Services;

/// <summary>
/// Windows 7 兼容性相关的运行时判断与说明文案。
/// </summary>
internal static class Win7Compat
{
    public static bool IsWindows7OrOlder =>
        OperatingSystem.IsWindows() && Environment.OSVersion.Version < new Version(6, 2);

    public const string RenderingDescription =
        "渲染：ANGLE(D3D11) → 软件渲染回退。\n" +
        "合成：RedirectionSurface（Win7 无 DirectComposition，Mica/Acrylic 不可用）。";

    public static string Describe() =>
        IsWindows7OrOlder
            ? "Windows 7 兼容模式：已强制兼容合成与 GPU/软件双渲染回退。"
            : "当前系统高于 Windows 7：仍使用兼容渲染配置，Win10/11 可改回默认合成模式。";
}