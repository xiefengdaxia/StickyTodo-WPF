using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.App.Services;

namespace StickyTodo.App.ViewModels;

/// <summary>设置页里的一张皮肤色卡。</summary>
public partial class SkinOptionViewModel : ObservableObject
{
    public SkinOptionViewModel(NoteSkin skin)
    {
        Skin = skin;
    }

    public NoteSkin Skin { get; }

    public string Name => Skin.DisplayName;

    public Brush Preview => Skin.CreateSwatchBrush();

    public Brush TextPreview => new SolidColorBrush(Skin.Ink);

    public bool IsDark => Skin.IsDark;

    [ObservableProperty]
    private bool _isSelected;
}