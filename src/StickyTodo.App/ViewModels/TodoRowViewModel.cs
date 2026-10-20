using StickyTodo.Core.Entities;

namespace StickyTodo.App.ViewModels;

/// <summary>列表里的一行。轻量包装，避免把领域对象直接暴露给界面绑定。</summary>
public sealed class TodoRowViewModel
{
    public TodoRowViewModel(TodoItem item, string tagText)
    {
        Item = item;
        TagText = tagText;
    }

    public TodoItem Item { get; }

    public string Id => Item.Id;

    public string Title => Item.Title;

    public bool IsDone => Item.IsDone;

    public string TagText { get; }

    public bool HasTags => !string.IsNullOrEmpty(TagText);
}