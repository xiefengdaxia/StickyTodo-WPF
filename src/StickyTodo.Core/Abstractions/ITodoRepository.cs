using StickyTodo.Core.Entities;

namespace StickyTodo.Core.Abstractions;

public interface ITodoRepository
{
    /// <summary>建库/建表（幂等），应用启动时调用一次。</summary>
    void Initialize();

    IReadOnlyList<Board> GetBoards();

    Board AddBoard(string name, bool isDefault = false);

    bool DeleteBoard(string boardId);

    IReadOnlyList<TodoItem> GetTodos(string boardId, bool includeDone = true);

    TodoItem? GetTodo(string id);

    TodoItem AddTodo(string boardId, string title, DateTimeOffset? dueAt = null);

    bool UpdateTodo(TodoItem item);

    bool DeleteTodo(string id);

    bool SetTodoDone(string id, bool done);
}