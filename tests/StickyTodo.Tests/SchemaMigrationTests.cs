using Microsoft.Data.Sqlite;
using StickyTodo.Core.Entities;
using StickyTodo.Infrastructure.Persistence;

namespace StickyTodo.Tests;

/// <summary>
/// 老版本数据库（user_version=1，无窗口位置/标签表）必须能自动迁移到 v2。
/// </summary>
public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"stickytodo-migrate-{Guid.NewGuid():N}.db");

    [Fact]
    public void V1Database_MigratesToV2_KeepingExistingData()
    {
        CreateLegacyV1Database();

        var repository = new SqliteTodoRepository(_databasePath);
        repository.Initialize();

        var boards = repository.GetBoards();
        var board = Assert.Single(boards);
        Assert.Equal("老看板", board.Name);
        Assert.Null(board.Window);

        var todo = Assert.Single(repository.GetTodos(board.Id));
        Assert.Equal("老待办", todo.Title);

        // v2 新增能力可用
        repository.SaveBoardWindow(board.Id, new BoardWindowState(10, 20, 300, 400));
        Assert.Equal(300, repository.GetBoards()[0].Window!.Value.Width);

        var tag = repository.AddTag("迁移后标签", TagColors.Default);
        repository.SetTodoTags(todo.Id, [tag.Id]);
        Assert.Single(repository.GetTodoTagMap(board.Id)[todo.Id]);

Assert.Equal(2L, ReadSchemaVersion());
    }

    private long ReadSchemaVersion()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = _databasePath };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void CreateLegacyV1Database()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = _databasePath };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE board (
                id         TEXT    NOT NULL PRIMARY KEY,
                name       TEXT    NOT NULL,
                sort       INTEGER NOT NULL DEFAULT 0,
                is_default INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE todo (
                id         TEXT    NOT NULL PRIMARY KEY,
                board_id   TEXT    NOT NULL,
                title      TEXT    NOT NULL,
                note       TEXT    NULL,
                is_done    INTEGER NOT NULL DEFAULT 0,
                done_at    INTEGER NULL,
                due_at     INTEGER NULL,
                sort       INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );
            INSERT INTO board (id, name, sort, is_default, created_at)
                VALUES ('b1', '老看板', 0, 1, 1700000000);
            INSERT INTO todo (id, board_id, title, is_done, sort, created_at, updated_at)
                VALUES ('t1', 'b1', '老待办', 0, 0, 1700000000, 1700000000);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // 收尾，忽略
            }
        }
    }
}