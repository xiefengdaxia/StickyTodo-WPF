using CommunityToolkit.Mvvm.ComponentModel;

namespace StickyTodo.App.ViewModels;

/// <summary>
/// 标签筛选胶囊。TagId 为 null 表示「全部」，空串表示「未分类」。
/// </summary>
public partial class TagChipViewModel : ObservableObject
{
    public const string NoTagId = "";

    public TagChipViewModel(string? tagId, string name, string color)
    {
        TagId = tagId;
        Name = name;
        Color = color;
    }

    public string? TagId { get; }

    public string Name { get; }

    public string Color { get; }

    public bool IsAll => TagId is null;

    public bool IsNoTag => TagId == NoTagId;

    public bool IsRealTag => TagId is not null && TagId != NoTagId;

    /// <summary>悬停提示：说清楚拖上去会发生什么。</summary>
    public string ChipToolTip => TagId switch
    {
        null => "显示全部待办；把待办拖到这里可清空它的标签",
        NoTagId => "只看没有标签的待办；把待办拖到这里可清空它的标签",
        _ => $"只看「{Name}」；把待办拖到这里可打上这个标签",
    };

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _count;
}