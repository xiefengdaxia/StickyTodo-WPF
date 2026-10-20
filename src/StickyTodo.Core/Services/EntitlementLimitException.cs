namespace StickyTodo.Core.Services;

/// <summary>触达权益上限时抛出，界面据此提示，而不是自己写判断。</summary>
public sealed class EntitlementLimitException : Exception
{
    public EntitlementLimitException(string message) : base(message)
    {
    }
}