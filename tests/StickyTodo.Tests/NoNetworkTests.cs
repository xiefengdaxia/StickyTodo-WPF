using StickyTodo.Core.Services;
using StickyTodo.Infrastructure.Persistence;

namespace StickyTodo.Tests;

/// <summary>
/// 「完全本地化」不是口头承诺：这里断言核心与基础设施层根本没有引用任何网络程序集。
/// </summary>
public sealed class NoNetworkTests
{
    private static readonly string[] ForbiddenAssemblies =
    {
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.Requests",
        "System.Net.WebClient",
    };

    [Fact]
    public void CoreAssembly_DoesNotReferenceNetworkApis()
    {
        AssertNoNetworkReferences(typeof(TodoService).Assembly.GetReferencedAssemblies().Select(a => a.Name));
    }

    [Fact]
    public void InfrastructureAssembly_DoesNotReferenceNetworkApis()
    {
        AssertNoNetworkReferences(typeof(SqliteTodoRepository).Assembly.GetReferencedAssemblies().Select(a => a.Name));
    }

    private static void AssertNoNetworkReferences(IEnumerable<string?> references)
    {
        var names = references.Where(n => n is not null).Select(n => n!).ToArray();
        foreach (var forbidden in ForbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden, names);
        }
    }
}