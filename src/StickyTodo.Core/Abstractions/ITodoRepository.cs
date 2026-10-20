using StickyTodo.Core.Entities;

namespace StickyTodo.Core.Abstractions;

public interface ITodoRepository
{
    /// <summary>建库/迁移（幂等），应用启动时调用一次。</summary>
    void Initialize();

    // ---- 看板 ----
    IReadOnlyList<Board> GetBoards();

    Board AddBoard(string name, BoardKind kind = BoardKind.Todo, bool isDefault = false);

    bool RenameBoard(string boardId, string name);

    bool DeleteBoard(string boardId);

    void SaveBoardWindow(string boardId, BoardWindowState state);

    // ---- 待办 ----
    IReadOnlyList<TodoItem> GetTodos(string boardId, bool includeDone = true);

    TodoItem? GetTodo(string id);

    TodoItem AddTodo(string boardId, string title, DateTimeOffset? dueAt = null);

    bool UpdateTodo(TodoItem item);

    bool DeleteTodo(string id);

    bool SetTodoDone(string id, bool done);

    /// <summary>按给定顺序重排该看板下所有待办。</summary>
    void ReorderTodos(string boardId, IReadOnlyList<string> orderedTodoIds);

    // ---- 标签 ----
    IReadOnlyList<Tag> GetTags();

    Tag AddTag(string name, string color);

    bool RenameTag(string tagId, string name);

    bool DeleteTag(string tagId);

    /// <summary>返回「待办 id → 标签 id 列表」，只取该看板下的待办。</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> GetTodoTagMap(string boardId);

    void SetTodoTags(string todoId, IReadOnlyList<string> tagIds);

    // ---- 设置（键值对，避免每加一个开关就改表结构）----
    IReadOnlyDictionary<string, string> GetSettings();

    void SetSetting(string key, string value);

    // ---- 便签/随笔正文 ----
    Note? GetNote(string boardId);

    Note SaveNote(string boardId, string content);

    // ---- 提醒 ----
    /// <summary>到点且还没提醒过的未完成待办。</summary>
    IReadOnlyList<TodoItem> GetDueReminders(DateTimeOffset now);

    bool SetRemindAt(string todoId, DateTimeOffset? remindAt);

    bool SetDueAt(string todoId, DateTimeOffset? dueAt);

    /// <summary>传 null 表示清空（改过提醒时间后要重置，好让它再次弹出）。</summary>
    bool MarkReminded(string todoId, DateTimeOffset? remindedAt);
}