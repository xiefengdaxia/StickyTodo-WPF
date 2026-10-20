namespace StickyTodo.App.Services;

/// <summary>
/// 便签窗口的外观能力：皮肤、背景透明度、整窗透明度。
/// 待办看板和便签看板的 ViewModel 都实现它，外观菜单就能共用一套代码。
/// </summary>
public interface IAppearanceHost
{
    string CurrentSkinKey { get; }

    double CurrentOpacity { get; }

    double CurrentWindowOpacity { get; }

    void SetSkin(string skinKey);

    void SetOpacity(double opacity);

    void SetWindowOpacity(double opacity);

    void RefreshAppearance();
}