namespace StickyTodo.Notes.Services;

/// <summary>
/// ViewModel 里由 C# 拼出来的文案（计数、状态行、标签名）不会随
/// <c>{loc:Loc}</c> 绑定自动刷新，语言切换时要显式重算。
/// </summary>
public interface IRefreshLocalized
{
    void RefreshLocalizedText();
}
