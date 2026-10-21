namespace StickyTodo.Infrastructure;

/// <summary>
/// 应用数据目录。三种模式，按优先级：
///
/// 1. **环境变量 <c>STICKYTODO_DATA_DIR</c>**（最高优先级）：演示、CI、多套数据隔离用。
/// 2. **便携模式**：可执行文件旁边存在 <c>portable.marker</c> 文件时，
///    数据放在 exe 同目录的 <c>data</c> 子目录里（U 盘、绿色版，拷走整个文件夹就能带走全部数据）。
///    用"标记文件"而不是"能不能写"来判定，避免把只读目录/网络盘误判成便携。
/// 3. **默认**：<c>%LOCALAPPDATA%\StickyTodo</c>。
///
/// 数据库与日志分开放，卸载/重装不会互相覆盖。
/// </summary>
public static class AppPaths
{
    public const string DataDirectoryEnvironmentVariable = "STICKYTODO_DATA_DIR";

    /// <summary>便携模式的标记文件名。放在 exe 同目录即可启用便携模式。</summary>
    public const string PortableMarkerFileName = "portable.marker";

    /// <summary>便携模式下数据放在 exe 同目录的这个子文件夹里。</summary>
    public const string PortableDataFolderName = "data";

    /// <summary>便携模式的判定结果，整进程只解析一次。</summary>
    private static readonly Lazy<PortableState> State = new(Detect);

    private static readonly Lazy<string> DataDirectoryLazy = new(ResolveDataDirectory);

    public static string DataDirectory => DataDirectoryLazy.Value;

    public static string DatabasePath => Path.Combine(DataDirectory, "sticky.db");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>当前是否运行在便携模式（数据跟着 exe 走）。</summary>
    public static bool IsPortable => State.Value.IsPortable;

    /// <summary>用户是否用环境变量显式指定了数据目录。</summary>
    public static bool IsOverriddenByEnvironment => State.Value.FromEnvironment;

    /// <summary>
    /// 可执行文件所在目录（单文件发布时就是 exe 旁边）。
    /// 注意：不能读 <see cref="State"/>——Detect() 内部会调用它，会形成循环。
    /// </summary>
    public static string ExecutableDirectory { get; } = ResolveBaseDirectory();

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private readonly record struct PortableState(
        bool IsPortable,
        bool FromEnvironment,
        string BaseDirectory);

    private static PortableState Detect()
    {
        var baseDirectory = ExecutableDirectory;
        var marker = Path.Combine(baseDirectory, PortableMarkerFileName);
        var isPortable = SafeFileExists(marker);

        return new PortableState(isPortable, false, baseDirectory);
    }

    /// <summary>
    /// 取 exe 所在目录。单文件发布时 <c>Assembly.Location</c> 是空串，
    /// 必须用 <c>Environment.ProcessPath</c>（.NET 6+ 才可靠）。
    /// </summary>
    private static string ResolveBaseDirectory()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var directory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }
        }
        catch (Exception)
        {
            // 极端情况下退回当前目录
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        var state = State.Value;
        if (state.IsPortable)
        {
            return Path.Combine(state.BaseDirectory, PortableDataFolderName);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StickyTodo");
    }
}