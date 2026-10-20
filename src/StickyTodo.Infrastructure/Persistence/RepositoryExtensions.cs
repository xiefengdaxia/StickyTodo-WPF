using Dapper;
using Microsoft.Data.Sqlite;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;

namespace StickyTodo.Infrastructure.Persistence;

/// <summary>
/// 便签正文、提醒、通用设置这三块读写。（主仓储 <see cref="SqliteTodoRepository"/> 的姊妹类，
/// 拆开是为了让单个文件不至于膨胀到难以阅读。）
/// </summary>
internal static class RepositoryExtensions
{
    internal static Note? GetNote(SqliteConnection connection, string boardId)
    {
        var row = connection.QuerySingleOrDefault<NoteRow>(
            "SELECT * FROM note WHERE board_id = @boardId;", new { boardId });

        return row is null
            ? null
            : new Note
            {
                Id = row.id,
                BoardId = row.board_id,
                Content = row.content,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(row.created_at).ToLocalTime(),
                UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(row.updated_at).ToLocalTime(),
            };
    }

    internal static Note SaveNote(SqliteConnection connection, string boardId, string content)
    {
        var now = DateTimeOffset.Now;
        var existing = GetNote(connection, boardId);

        if (existing is null)
        {
            var note = new Note
            {
                Id = Guid.NewGuid().ToString("N"),
                BoardId = boardId,
                Content = content,
                CreatedAt = now,
                UpdatedAt = now,
            };

            connection.Execute(
                "INSERT INTO note (id, board_id, content, created_at, updated_at) VALUES (@Id, @BoardId, @Content, @CreatedAt, @UpdatedAt);",
                new
                {
                    note.Id,
                    note.BoardId,
                    note.Content,
                    CreatedAt = note.CreatedAt.ToUnixTimeSeconds(),
                    UpdatedAt = note.UpdatedAt.ToUnixTimeSeconds(),
                });

            return note;
        }

        existing.Content = content;
        existing.UpdatedAt = now;
        connection.Execute(
            "UPDATE note SET content = @Content, updated_at = @UpdatedAt WHERE board_id = @boardId;",
            new { existing.Content, UpdatedAt = existing.UpdatedAt.ToUnixTimeSeconds(), boardId });

        return existing;
    }

    private sealed class NoteRow
    {
        public string id { get; set; } = string.Empty;
        public string board_id { get; set; } = string.Empty;
        public string content { get; set; } = string.Empty;
        public long created_at { get; set; }
        public long updated_at { get; set; }
    }
}