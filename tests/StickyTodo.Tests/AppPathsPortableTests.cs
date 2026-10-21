using StickyTodo.Infrastructure;

namespace StickyTodo.Tests;

/// <summary>
/// 便携模式判定测试。
/// 注意：<see cref="AppPaths"/> 的判定是进程级 Lazy（只解析一次），
/// 所以这里只测"判定逻辑的规则"，不去改它的内部状态——
/// 真正的端到端验证由 <c>--portable-demo</c> 在独立进程里完成。
/// </summary>
public sealed class AppPathsPortableTests
{
    [Fact]
    public void MarkerFileName_IsStable()
    {
        // 发布脚本和文档都依赖这个文件名，改了就破坏兼容
        Assert.Equal("portable.marker", AppPaths.PortableMarkerFileName);
        Assert.Equal("data", AppPaths.PortableDataFolderName);
    }

    [Fact]
    public void EnvironmentVariableName_IsStable()
    {
        Assert.Equal("STICKYTODO_DATA_DIR", AppPaths.DataDirectoryEnvironmentVariable);
    }

    [Fact]
    public void ExecutableDirectory_IsNotNullOrEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppPaths.ExecutableDirectory));
        Assert.True(Directory.Exists(AppPaths.ExecutableDirectory),
            $"可执行文件目录应存在：{AppPaths.ExecutableDirectory}");
    }

    [Fact]
    public void DataDirectory_IsAbsolute()
    {
        Assert.True(Path.IsPathRooted(AppPaths.DataDirectory));
    }

    [Fact]
    public void DatabaseAndLogs_LiveUnderDataDirectory()
    {
        Assert.StartsWith(AppPaths.DataDirectory, AppPaths.DatabasePath);
        Assert.StartsWith(AppPaths.DataDirectory, AppPaths.LogDirectory);
        Assert.EndsWith("sticky.db", AppPaths.DatabasePath);
        Assert.EndsWith("logs", AppPaths.LogDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void EnsureCreated_CreatesDataAndLogFolders()
    {
        AppPaths.EnsureCreated();

        Assert.True(Directory.Exists(AppPaths.DataDirectory));
        Assert.True(Directory.Exists(AppPaths.LogDirectory));
    }

    [Fact]
    public void IsPortable_IsFalseWithoutMarkerInTestHost()
    {
        // 测试宿主目录里没有 portable.marker，应判为非便携
        var marker = Path.Combine(AppPaths.ExecutableDirectory, AppPaths.PortableMarkerFileName);
        if (File.Exists(marker))
        {
            // 万一测试是在便携目录里跑的，这条断言就不成立，直接跳过
            return;
        }

        Assert.False(AppPaths.IsPortable);
    }
}