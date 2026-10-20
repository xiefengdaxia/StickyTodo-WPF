using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;

namespace StickyTodo.Core.Services;

/// <summary>
/// 业务用例层：所有写操作先过权益闸门，再落仓库。
/// 界面只调这里，不直接碰仓储。
/// </summary>
public sealed class TodoService
{
    public const string DefaultBoardName = "我的待办";

    private readonly ITodoRepository _repository;
    private readonly IEntitlementService _entitlement;

    public TodoService(ITodoRepository repository, IEntitlementService entitlement)
    {
        _repository = repository;
        _entitlement = entitlement;
    }

    public IEntitlementService Entitlement => _entitlement;

    /// <summary>首次启动时保证至少有一个默认看板。</summary>
    public Board EnsureDefaultBoard()
    {
        var boards = _repository.GetBoards();
        if (boards.Count > 0)
        {
            return boards.FirstOrDefault(b => b.IsDefault) ?? boards[0];
        }

        return _repository.AddBoard(DefaultBoardName, isDefault: true);
    }

    public IReadOnlyList<Board> GetBoards() => _repository.GetBoards();

    public Board CreateBoard(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("看板名称不能为空。", nameof(name));
        }

        var current = _repository.GetBoards().Count;
        if (current >= _entitlement.MaxBoards)
        {
            throw new EntitlementLimitException(
                $"{_entitlement.DisplayName}最多支持 {_entitlement.MaxBoards} 个看板，当前已达上限。");
        }

        return _repository.AddBoard(name.Trim());
    }

    public bool DeleteBoard(string boardId) => _repository.DeleteBoard(boardId);

    public IReadOnlyList<TodoItem> GetTodos(string boardId, bool includeDone = true) =>
        _repository.GetTodos(boardId, includeDone);

    public TodoItem AddTodo(string boardId, string title, DateTimeOffset? dueAt = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("待办内容不能为空。", nameof(title));
        }

        return _repository.AddTodo(boardId, title.Trim(), dueAt);
    }

    public void SetDone(string id, bool done) => _repository.SetTodoDone(id, done);

    public void Delete(string id) => _repository.DeleteTodo(id);

    public void UpdateNote(string id, string? note)
    {
        var item = _repository.GetTodo(id) ?? throw new InvalidOperationException($"待办不存在：{id}");
        item.Note = note;
        item.UpdatedAt = DateTimeOffset.Now;
        _repository.UpdateTodo(item);
    }
}