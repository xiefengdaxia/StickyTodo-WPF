namespace StickyTodo.Infrastructure;

/// <summary>
/// 应用数据目录：%LOCALAPPDATA%\StickyTodo。
/// 数据库与日志分开放，卸载/重装不会互相覆盖。
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StickyTodo");

    public static string DatabasePath => Path.Combine(DataDirectory, "sticky.db");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}