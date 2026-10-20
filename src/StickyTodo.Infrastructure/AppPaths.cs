namespace StickyTodo.Infrastructure;

/// <summary>
/// 应用数据目录：默认 %LOCALAPPDATA%\StickyTodo。
/// 数据库与日志分开放，卸载/重装不会互相覆盖。
/// 支持用环境变量 STICKYTODO_DATA_DIR 覆盖（便携模式 / 演示 / CI 用独立数据目录）。
/// </summary>
public static class AppPaths
{
    public const string DataDirectoryEnvironmentVariable = "STICKYTODO_DATA_DIR";

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string DatabasePath => Path.Combine(DataDirectory, "sticky.db");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StickyTodo");
    }
}