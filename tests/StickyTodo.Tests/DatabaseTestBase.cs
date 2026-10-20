using StickyTodo.Infrastructure.Persistence;

namespace StickyTodo.Tests;

/// <summary>每个测试用独立临时库，互不干扰。</summary>
public abstract class DatabaseTestBase : IDisposable
{
    private readonly string _databasePath;

    protected SqliteTodoRepository Repository { get; }

    protected DatabaseTestBase()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"stickytodo-test-{Guid.NewGuid():N}.db");
        Repository = new SqliteTodoRepository(_databasePath);
        Repository.Initialize();
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // 测试收尾，删不掉不影响结论
            }
        }
    }
}