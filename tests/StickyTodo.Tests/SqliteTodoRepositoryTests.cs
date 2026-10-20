namespace StickyTodo.Tests;

public sealed class SqliteTodoRepositoryTests : DatabaseTestBase
{
    [Fact]
    public void Initialize_IsIdempotent()
    {
        Repository.Initialize();
        Repository.Initialize();

        Assert.Empty(Repository.GetBoards());
    }

    [Fact]
    public void AddAndCompleteTodo_RoundTrips()
    {
        var board = Repository.AddBoard("测试看板", isDefault: true);

        var todo = Repository.AddTodo(board.Id, "写第一条待办");
        Assert.False(todo.IsDone);
        Assert.Equal(board.Id, todo.BoardId);

        var loaded = Repository.GetTodo(todo.Id);
        Assert.NotNull(loaded);
        Assert.Equal("写第一条待办", loaded!.Title);

        Assert.True(Repository.SetTodoDone(todo.Id, true));
        var done = Repository.GetTodo(todo.Id)!;
        Assert.True(done.IsDone);
        Assert.NotNull(done.DoneAt);

        Assert.Empty(Repository.GetTodos(board.Id, includeDone: false));
        Assert.Single(Repository.GetTodos(board.Id, includeDone: true));

        Assert.True(Repository.DeleteTodo(todo.Id));
        Assert.Null(Repository.GetTodo(todo.Id));
    }

    [Fact]
    public void DeleteBoard_CascadesItsTodos()
    {
        var board = Repository.AddBoard("临时看板");
        Repository.AddTodo(board.Id, "会被连带删除");

        Assert.True(Repository.DeleteBoard(board.Id));
        Assert.Empty(Repository.GetBoards());
        Assert.Empty(Repository.GetTodos(board.Id));
    }

    [Fact]
    public void Timestamps_SurviveRoundTrip()
    {
        var board = Repository.AddBoard("时间测试");
        var due = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));

        var todo = Repository.AddTodo(board.Id, "带截止时间", due);
        var loaded = Repository.GetTodo(todo.Id)!;

        Assert.Equal(due, loaded.DueAt);
        Assert.Equal(todo.CreatedAt.ToUnixTimeSeconds(), loaded.CreatedAt.ToUnixTimeSeconds());
    }
}