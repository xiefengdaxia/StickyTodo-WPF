using Dapper;
using Microsoft.Data.Sqlite;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.Infrastructure.Persistence;

/// <summary>
/// SQLite 实现。一次操作一个连接（连接池自动复用），不持有长连接，避免 WPF 多窗口下的线程问题。
/// </summary>
public sealed class SqliteTodoRepository : ITodoRepository
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;

    public SqliteTodoRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            // WAL：读写并发更好，崩溃后不易损坏
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };
        _connectionString = builder.ToString();
    }

    public void Initialize()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode=WAL;");
        connection.Execute("PRAGMA synchronous=NORMAL;");

        var version = connection.ExecuteScalar<long>("PRAGMA user_version;");
        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        using var tx = connection.BeginTransaction();
        connection.Execute(SchemaV1, transaction: tx);
        connection.Execute($"PRAGMA user_version={CurrentSchemaVersion};", transaction: tx);
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS board (
            id         TEXT    NOT NULL PRIMARY KEY,
            name       TEXT    NOT NULL,
            sort       INTEGER NOT NULL DEFAULT 0,
            is_default INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS todo (
            id         TEXT    NOT NULL PRIMARY KEY,
            board_id   TEXT    NOT NULL,
            title      TEXT    NOT NULL,
            note       TEXT    NULL,
            is_done    INTEGER NOT NULL DEFAULT 0,
            done_at    INTEGER NULL,
            due_at     INTEGER NULL,
            sort       INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            FOREIGN KEY (board_id) REFERENCES board(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_todo_board ON todo (board_id, is_done, sort);
        """;

    public IReadOnlyList<Board> GetBoards()
    {
        using var connection = Open();
        return connection.Query<BoardRow>(
                "SELECT id, name, sort, is_default, created_at FROM board ORDER BY sort, created_at;")
            .Select(ToBoard)
            .ToList();
    }

    public Board AddBoard(string name, bool isDefault = false)
    {
        using var connection = Open();
        var sort = connection.ExecuteScalar<int>("SELECT COALESCE(MAX(sort), -1) + 1 FROM board;");
        var board = new Board
        {
            Id = NewId(),
            Name = name,
            Sort = sort,
            IsDefault = isDefault,
            CreatedAt = DateTimeOffset.Now,
        };

        connection.Execute(
            "INSERT INTO board (id, name, sort, is_default, created_at) VALUES (@Id, @Name, @Sort, @IsDefault, @CreatedAt);",
            new
            {
                board.Id,
                board.Name,
                board.Sort,
                IsDefault = isDefault ? 1 : 0,
                CreatedAt = ToUnix(board.CreatedAt),
            });

        return board;
    }

    public bool DeleteBoard(string boardId)
    {
        using var connection = Open();
        return connection.Execute("DELETE FROM board WHERE id = @boardId;", new { boardId }) > 0;
    }

    public IReadOnlyList<TodoItem> GetTodos(string boardId, bool includeDone = true)
    {
        using var connection = Open();
        var sql = includeDone
            ? "SELECT * FROM todo WHERE board_id = @boardId ORDER BY is_done, sort, created_at;"
            : "SELECT * FROM todo WHERE board_id = @boardId AND is_done = 0 ORDER BY sort, created_at;";

        return connection.Query<TodoRow>(sql, new { boardId }).Select(ToTodo).ToList();
    }

    public TodoItem? GetTodo(string id)
    {
        using var connection = Open();
        var row = connection.QuerySingleOrDefault<TodoRow>("SELECT * FROM todo WHERE id = @id;", new { id });
        return row is null ? null : ToTodo(row);
    }

    public TodoItem AddTodo(string boardId, string title, DateTimeOffset? dueAt = null)
    {
        using var connection = Open();
        var now = DateTimeOffset.Now;
        var sort = connection.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(sort), -1) + 1 FROM todo WHERE board_id = @boardId;", new { boardId });

        var item = new TodoItem
        {
            Id = NewId(),
            BoardId = boardId,
            Title = title,
            IsDone = false,
            DueAt = dueAt,
            Sort = sort,
            CreatedAt = now,
            UpdatedAt = now,
        };

        connection.Execute(
            """
            INSERT INTO todo (id, board_id, title, note, is_done, done_at, due_at, sort, created_at, updated_at)
            VALUES (@Id, @BoardId, @Title, @Note, @IsDone, @DoneAt, @DueAt, @Sort, @CreatedAt, @UpdatedAt);
            """,
            new
            {
                item.Id,
                item.BoardId,
                item.Title,
                item.Note,
                IsDone = 0,
                DoneAt = (long?)null,
                DueAt = ToUnix(item.DueAt),
                item.Sort,
                CreatedAt = ToUnix(item.CreatedAt),
                UpdatedAt = ToUnix(item.UpdatedAt),
            });

        return item;
    }

    public bool UpdateTodo(TodoItem item)
    {
        using var connection = Open();
        return connection.Execute(
            """
            UPDATE todo
               SET title = @Title,
                   note = @Note,
                   is_done = @IsDone,
                   done_at = @DoneAt,
                   due_at = @DueAt,
                   sort = @Sort,
                   updated_at = @UpdatedAt
             WHERE id = @Id;
            """,
            new
            {
                item.Id,
                item.Title,
                item.Note,
                IsDone = item.IsDone ? 1 : 0,
                DoneAt = ToUnix(item.DoneAt),
                DueAt = ToUnix(item.DueAt),
                item.Sort,
                UpdatedAt = ToUnix(item.UpdatedAt),
            }) > 0;
    }

    public bool DeleteTodo(string id)
    {
        using var connection = Open();
        return connection.Execute("DELETE FROM todo WHERE id = @id;", new { id }) > 0;
    }

    public bool SetTodoDone(string id, bool done)
    {
        using var connection = Open();
        return connection.Execute(
            "UPDATE todo SET is_done = @done, done_at = @doneAt, updated_at = @now WHERE id = @id;",
            new
            {
                id,
                done = done ? 1 : 0,
                doneAt = done ? (long?)ToUnix(DateTimeOffset.Now) : null,
                now = ToUnix(DateTimeOffset.Now),
            }) > 0;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static long ToUnix(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private static long? ToUnix(DateTimeOffset? value) => value?.ToUnixTimeSeconds();

    private static DateTimeOffset FromUnix(long value) => DateTimeOffset.FromUnixTimeSeconds(value);

    private static DateTimeOffset? FromUnix(long? value) =>
        value is null ? null : DateTimeOffset.FromUnixTimeSeconds(value.Value);

    private static Board ToBoard(BoardRow row) => new()
    {
        Id = row.id,
        Name = row.name,
        Sort = row.sort,
        IsDefault = row.is_default != 0,
        CreatedAt = FromUnix(row.created_at),
    };

    private static TodoItem ToTodo(TodoRow row) => new()
    {
        Id = row.id,
        BoardId = row.board_id,
        Title = row.title,
        Note = row.note,
        IsDone = row.is_done != 0,
        DoneAt = FromUnix(row.done_at),
        DueAt = FromUnix(row.due_at),
        Sort = row.sort,
        CreatedAt = FromUnix(row.created_at),
        UpdatedAt = FromUnix(row.updated_at),
    };

    // Dapper 的列映射对象（snake_case → 字段名要和 SELECT * 的列名一致）
    private sealed class BoardRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public int sort { get; set; }
        public int is_default { get; set; }
        public long created_at { get; set; }
    }

    private sealed class TodoRow
    {
        public string id { get; set; } = string.Empty;
        public string board_id { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string? note { get; set; }
        public int is_done { get; set; }
        public long? done_at { get; set; }
        public long? due_at { get; set; }
        public int sort { get; set; }
        public long created_at { get; set; }
        public long updated_at { get; set; }
    }
}