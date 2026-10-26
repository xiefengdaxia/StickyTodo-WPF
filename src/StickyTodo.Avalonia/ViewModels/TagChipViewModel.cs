using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Localization;

namespace StickyTodo.Notes.ViewModels;

/// <summary>
/// 标签筛选胶囊。TagId 为 null 表示「全部」，空串表示「未分类」。
/// </summary>
public partial class TagChipViewModel : ObservableObject
{
    public const string NoTagId = "";

    public TagChipViewModel(string? tagId, string name, string color)
    {
        TagId = tagId;
        _name = name;
        Color = color;
    }

    public string? TagId { get; }

    /// <summary>显示名。内置的「全部 / 未分类」会随语言变化，所以是可写属性。</summary>
    public string Name
    {
        get => _name;
        private set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChipToolTip));
        }
    }

    private string _name;

    /// <summary>切换语言时更新显示名。</summary>
    public void SetName(string name) => Name = name;

    /// <summary>标签颜色（十六进制串，界面用转换器变成画刷）。</summary>
    public string Color { get; }

    public bool IsAll => TagId is null;

    public bool IsNoTag => TagId == NoTagId;

    public bool IsRealTag => TagId is not null && TagId != NoTagId;

    /// <summary>悬停提示：说清楚拖上去会发生什么。</summary>
    public string ChipToolTip => TagId switch
    {
        null => Loc.T("tag.allTip"),
        NoTagId => Loc.T("tag.noneTip"),
        _ => Loc.F("tag.someTip", Name),
    };

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>拖动中的行悬停在这个胶囊上（高亮提示"松手就打上这个标签"）。</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    [ObservableProperty]
    private int _count;
}
