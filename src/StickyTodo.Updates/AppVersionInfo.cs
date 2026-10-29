using System.Reflection;

namespace StickyTodo.Updates;

/// <summary>
/// 当前运行版本。优先用 <see cref="AssemblyInformationalVersionAttribute"/>
/// （发布脚本会传 <c>-p:Version=x.y.z</c>，会连同伴随的 +git-sha 一起写进去），
/// 拿不到再退回文件版本。
/// </summary>
public static class AppVersionInfo
{
    /// <summary>本进程的版本号。</summary>
    public static Version Current { get; } =
        ReadFrom(Assembly.GetEntryAssembly() ?? typeof(AppVersionInfo).Assembly);

    public static Version ReadFrom(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParse(informational, out var version))
        {
            return version;
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        return TryParse(fileVersion, out version) ? version : new Version(0, 0, 0);
    }

    /// <summary>
    /// 解析版本文本。能吃掉这些写法：<c>1.5.6</c>、<c>v1.5.6</c>、
    /// <c>1.5.6+abc123</c>、<c>1.5.6-beta.1</c>、<c>release-1.5</c>（→ 1.5.0）。
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();

        // 只认「数字打头的尾巴」：v1.5.6 / release-1.5 / 1.5.6+sha
        var start = 0;
        while (start < span.Length && !char.IsDigit(span[start]))
        {
            start++;
        }

        if (start >= span.Length)
        {
            return false;
        }

        span = span[start..];

        // 版本号后面跟的预发布标记 / 元数据 / 空格都截掉
        var cut = span.IndexOfAny(['-', '+', ' ', '\t', '_']);
        if (cut > 0)
        {
            span = span[..cut];
        }

        var parts = span.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4)
        {
            return false;
        }

        var numbers = new List<int>(4);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number) || number < 0)
            {
                return false;
            }

            numbers.Add(number);
        }

        while (numbers.Count < 3)
        {
            numbers.Add(0);
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>版本号显示文本：1.5.6。</summary>
    public static string Display(Version? version) => version switch
    {
        null => "-",
        { Build: >= 0 } v => v.ToString(3),
        _ => version.ToString(2),
    };
}
