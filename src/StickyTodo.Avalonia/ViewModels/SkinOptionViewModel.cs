using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.ViewModels;

/// <summary>设置页里的一张皮肤色卡。</summary>
public partial class SkinOptionViewModel : ObservableObject
{
    public SkinOptionViewModel(NoteSkin skin)
    {
        Skin = skin;
    }

    public NoteSkin Skin { get; }

    public string Name => Skin.DisplayName;

    public IBrush Preview => Skin.CreateSwatchBrush();

    public IBrush TextPreview => new SolidColorBrush(Skin.Ink);

    public bool IsDark => Skin.IsDark;

    [ObservableProperty]
    private bool _isSelected;
}
