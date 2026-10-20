using Dapper;
using Microsoft.Data.Sqlite;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.Infrastructure.Persistence;

/// <summary>
/// SQLite 实现。一次操作一个连接（连接池自动复用），不持有长连接，
/// 避免 WPF 多窗口/后台线程下的竞态。
/// 迁移用 PRAGMA user_version 递增，全部本地执行，无任何网络参与。
/// </summary>
public sealed class SqliteTodoRepository : ITodoRepository
{
    private const int CurrentSchemaVersion = 4;

    private readonly string _connectionString;

    public SqliteTodoRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
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

        if (version < 1)
        {
            connection.Execute(SchemaV1, transaction: tx);
        }

        if (version < 2)
        {
            connection.Execute(SchemaV2, transaction: tx);
        }

        if (version < 3)
        {
            connection.Execute(SchemaV3, transaction: tx);
        }

        if (version < 4)
        {
            connection.Execute(SchemaV4, transaction: tx);
        }

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

    private const string SchemaV2 = """
        -- 每个看板窗口的位置与大小
        ALTER TABLE board ADD COLUMN win_left   REAL NULL;
        ALTER TABLE board ADD COLUMN win_top    REAL NULL;
        ALTER TABLE board ADD COLUMN win_width  REAL NULL;
        ALTER TABLE board ADD COLUMN win_height REAL NULL;

        CREATE TABLE IF NOT EXISTS tag (
            id         TEXT    NOT NULL PRIMARY KEY,
            name       TEXT    NOT NULL,
            color      TEXT    NOT NULL,
            sort       INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_tag_name ON tag (name COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS todo_tag (
            todo_id TEXT NOT NULL,
            tag_id  TEXT NOT NULL,
            PRIMARY KEY (todo_id, tag_id),
            FOREIGN KEY (todo_id) REFERENCES todo(id) ON DELETE CASCADE,
            FOREIGN KEY (tag_id)  REFERENCES tag(id)  ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_todo_tag_tag ON todo_tag (tag_id);
        """;

    private const string SchemaV3 = """
        -- 通用设置表：外观、每个看板的置顶/锁定等
        CREATE TABLE IF NOT EXISTS setting (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    private const string SchemaV4 = """
        -- 看板类型：todo=待办清单，memo=便签/随笔
        ALTER TABLE board ADD COLUMN kind TEXT NOT NULL DEFAULT 'todo';

        -- 提醒：到点弹窗；reminded_at 记录已提醒过，避免重复打扰
        ALTER TABLE todo ADD COLUMN remind_at   INTEGER NULL;
        ALTER TABLE todo ADD COLUMN reminded_at INTEGER NULL;

        CREATE INDEX IF NOT EXISTS ix_todo_remind ON todo (remind_at, reminded_at);

        -- 便签/随笔正文，一个看板一条
        CREATE TABLE IF NOT EXISTS note (
            id         TEXT    NOT NULL PRIMARY KEY,
            board_id   TEXT    NOT NULL,
            content    TEXT    NOT NULL DEFAULT '',
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            FOREIGN KEY (board_id) REFERENCES board(id) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_note_board ON note (board_id);
        """;

    // ---------------- 看板 ----------------

    public IReadOnlyList<Board> GetBoards()
    {
        using var connection = Open();
        return connection.Query<BoardRow>(
                """
                SELECT id, name, sort, is_default, kind, created_at,
                       win_left, win_top, win_width, win_height
                  FROM board
                 ORDER BY sort, created_at;
                """)
            .Select(ToBoard)
            .ToList();
    }

    public Board AddBoard(string name, BoardKind kind = BoardKind.Todo, bool isDefault = false)
    {
        using var connection = Open();
        var sort = connection.ExecuteScalar<int>("SELECT COALESCE(MAX(sort), -1) + 1 FROM board;");
        var board = new Board
        {
            Id = NewId(),
            Name = name,
            Sort = sort,
            IsDefault = isDefault,
            Kind = kind,
            CreatedAt = DateTimeOffset.Now,
        };

        connection.Execute(
            """
            INSERT INTO board (id, name, sort, is_default, kind, created_at)
            VALUES (@Id, @Name, @Sort, @IsDefault, @Kind, @CreatedAt);
            """,
            new
            {
                board.Id,
                board.Name,
                board.Sort,
                IsDefault = isDefault ? 1 : 0,
                Kind = BoardKinds.ToStorage(kind),
                CreatedAt = ToUnix(board.CreatedAt),
            });

        return board;
    }

    public bool RenameBoard(string boardId, string name)
    {
        using var connection = Open();
        return connection.Execute(
            "UPDATE board SET name = @name WHERE id = @boardId;", new { boardId, name }) > 0;
    }

    public bool DeleteBoard(string boardId)
    {
        using var connection = Open();
        return connection.Execute("DELETE FROM board WHERE id = @boardId;", new { boardId }) > 0;
    }

    public void SaveBoardWindow(string boardId, BoardWindowState state)
    {
        using var connection = Open();
        connection.Execute(
            """
            UPDATE board
               SET win_left = @Left, win_top = @Top, win_width = @Width, win_height = @Height
             WHERE id = @boardId;
            """,
            new { boardId, state.Left, state.Top, state.Width, state.Height });
    }

    // ---------------- 待办 ----------------

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
                   remind_at = @RemindAt,
                   reminded_at = @RemindedAt,
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
                RemindAt = ToUnix(item.RemindAt),
                RemindedAt = ToUnix(item.RemindedAt),
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

    public void ReorderTodos(string boardId, IReadOnlyList<string> orderedTodoIds)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        for (var i = 0; i < orderedTodoIds.Count; i++)
        {
            connection.Execute(
                "UPDATE todo SET sort = @i WHERE id = @id AND board_id = @boardId;",
                new { i, id = orderedTodoIds[i], boardId },
                transaction: tx);
        }

        tx.Commit();
    }

    // ---------------- 标签 ----------------

    public IReadOnlyList<Tag> GetTags()
    {
        using var connection = Open();
        return connection.Query<TagRow>("SELECT * FROM tag ORDER BY sort, name;")
            .Select(r => new Tag
            {
                Id = r.id,
                Name = r.name,
                Color = string.IsNullOrWhiteSpace(r.color) ? TagColors.Default : r.color,
                Sort = r.sort,
                CreatedAt = FromUnix(r.created_at),
            })
            .ToList();
    }

    public Tag AddTag(string name, string color)
    {
        using var connection = Open();
        var sort = connection.ExecuteScalar<int>("SELECT COALESCE(MAX(sort), -1) + 1 FROM tag;");
        var tag = new Tag
        {
            Id = NewId(),
            Name = name,
            Color = color,
            Sort = sort,
            CreatedAt = DateTimeOffset.Now,
        };

        connection.Execute(
            "INSERT INTO tag (id, name, color, sort, created_at) VALUES (@Id, @Name, @Color, @Sort, @CreatedAt);",
            new { tag.Id, tag.Name, tag.Color, tag.Sort, CreatedAt = ToUnix(tag.CreatedAt) });

        return tag;
    }

    public bool RenameTag(string tagId, string name)
    {
        using var connection = Open();
        return connection.Execute("UPDATE tag SET name = @name WHERE id = @tagId;", new { tagId, name }) > 0;
    }

    public bool DeleteTag(string tagId)
    {
        using var connection = Open();
        return connection.Execute("DELETE FROM tag WHERE id = @tagId;", new { tagId }) > 0;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetTodoTagMap(string boardId)
    {
        using var connection = Open();
        var rows = connection.Query<TodoTagRow>(
            """
            SELECT tt.todo_id AS todo_id, tt.tag_id AS tag_id
              FROM todo_tag tt
              JOIN todo t ON t.id = tt.todo_id
             WHERE t.board_id = @boardId;
            """,
            new { boardId });

        return rows
            .GroupBy(r => r.todo_id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.tag_id).ToList());
    }

    public void SetTodoTags(string todoId, IReadOnlyList<string> tagIds)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        connection.Execute("DELETE FROM todo_tag WHERE todo_id = @todoId;", new { todoId }, tx);

        foreach (var tagId in tagIds.Distinct())
        {
            connection.Execute(
                "INSERT OR IGNORE INTO todo_tag (todo_id, tag_id) VALUES (@todoId, @tagId);",
                new { todoId, tagId },
                tx);
        }

        tx.Commit();
    }

    // ---------------- 便签正文 / 提醒 / 设置 ----------------

    public Note? GetNote(string boardId)
    {
        using var connection = Open();
        return RepositoryExtensions.GetNote(connection, boardId);
    }

    public Note SaveNote(string boardId, string content)
    {
        using var connection = Open();
        return RepositoryExtensions.SaveNote(connection, boardId, content);
    }

    public IReadOnlyList<TodoItem> GetDueReminders(DateTimeOffset now)
    {
        using var connection = Open();
        return connection.Query<TodoRow>(
                """
                SELECT * FROM todo
                 WHERE is_done = 0
                   AND remind_at IS NOT NULL
                   AND reminded_at IS NULL
                   AND remind_at <= @now
                 ORDER BY remind_at;
                """,
                new { now = ToUnix(now) })
            .Select(ToTodo)
            .ToList();
    }

    public bool SetRemindAt(string todoId, DateTimeOffset? remindAt)
    {
        using var connection = Open();
        return connection.Execute(
            "UPDATE todo SET remind_at = @remindAt, updated_at = @now WHERE id = @todoId;",
            new { todoId, remindAt = ToUnix(remindAt), now = ToUnix(DateTimeOffset.Now) }) > 0;
    }

    public bool SetDueAt(string todoId, DateTimeOffset? dueAt)
    {
        using var connection = Open();
        return connection.Execute(
            "UPDATE todo SET due_at = @dueAt, updated_at = @now WHERE id = @todoId;",
            new { todoId, dueAt = ToUnix(dueAt), now = ToUnix(DateTimeOffset.Now) }) > 0;
    }

    public bool MarkReminded(string todoId, DateTimeOffset? remindedAt)
    {
        using var connection = Open();
        return connection.Execute(
            "UPDATE todo SET reminded_at = @remindedAt WHERE id = @todoId;",
            new { todoId, remindedAt = ToUnix(remindedAt) }) > 0;
    }

    public IReadOnlyDictionary<string, string> GetSettings()
    {
        using var connection = Open();
        return connection
            .Query<SettingRow>("SELECT key, value FROM setting;")
            .ToDictionary(row => row.key, row => row.value);
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        connection.Execute(
            "INSERT INTO setting (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            new { key, value });
    }

    // ---------------- 映射 ----------------

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static long ToUnix(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private static long? ToUnix(DateTimeOffset? value) => value?.ToUnixTimeSeconds();

    private static DateTimeOffset FromUnix(long value) =>
        DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime();

    private static DateTimeOffset? FromUnix(long? value) =>
        value is null ? null : DateTimeOffset.FromUnixTimeSeconds(value.Value).ToLocalTime();

    private static Board ToBoard(BoardRow row)
    {
        BoardWindowState? window = null;
        if (row.win_left is not null && row.win_top is not null &&
            row.win_width is not null && row.win_height is not null)
        {
            var state = new BoardWindowState(
                row.win_left.Value, row.win_top.Value, row.win_width.Value, row.win_height.Value);
            window = state.IsValid ? state : null;
        }

        return new Board
        {
            Id = row.id,
            Name = row.name,
            Sort = row.sort,
            IsDefault = row.is_default != 0,
            Kind = BoardKinds.Parse(row.kind),
            CreatedAt = FromUnix(row.created_at),
            Window = window,
        };
    }

    private static TodoItem ToTodo(TodoRow row) => new()
    {
        Id = row.id,
        BoardId = row.board_id,
        Title = row.title,
        Note = row.note,
        IsDone = row.is_done != 0,
        DoneAt = FromUnix(row.done_at),
        DueAt = FromUnix(row.due_at),
        RemindAt = FromUnix(row.remind_at),
        RemindedAt = FromUnix(row.reminded_at),
        Sort = row.sort,
        CreatedAt = FromUnix(row.created_at),
        UpdatedAt = FromUnix(row.updated_at),
    };

    // Dapper 映射对象：字段名要和 SELECT 出来的列名一致
    private sealed class BoardRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public int sort { get; set; }
        public int is_default { get; set; }
        public string kind { get; set; } = BoardKinds.Todo;
        public long created_at { get; set; }
        public double? win_left { get; set; }
        public double? win_top { get; set; }
        public double? win_width { get; set; }
        public double? win_height { get; set; }
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
        public long? remind_at { get; set; }
        public long? reminded_at { get; set; }
        public int sort { get; set; }
        public long created_at { get; set; }
        public long updated_at { get; set; }
    }

    private sealed class TagRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string color { get; set; } = string.Empty;
        public int sort { get; set; }
        public long created_at { get; set; }
    }

    private sealed class TodoTagRow
    {
        public string todo_id { get; set; } = string.Empty;
        public string tag_id { get; set; } = string.Empty;
    }

    private sealed class SettingRow
    {
        public string key { get; set; } = string.Empty;
        public string value { get; set; } = string.Empty;
    }
}