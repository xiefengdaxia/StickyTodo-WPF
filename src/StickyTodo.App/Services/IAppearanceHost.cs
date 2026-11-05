namespace StickyTodo.App.Services;

/// <summary>
/// 便签窗口的外观能力：皮肤、背景透明度、整窗透明度。
/// 待办看板和便签看板的 ViewModel 都实现它，外观菜单就能共用一套代码。
/// </summary>
public interface IAppearanceHost
{
    /// <summary>外观设置存在哪个 key 下（看板用 id，工具窗用固定 key）。</summary>
    string AppearanceKey { get; }

    string CurrentSkinKey { get; }

    double CurrentOpacity { get; }

    /// <summary>本窗口自己的整窗透明度（没单独设过就跟随全局）。</summary>
    double CurrentWindowOpacity { get; }

    /// <summary>这个窗口有没有单独设过透明度（false = 跟随全局）。</summary>
    bool HasOwnWindowOpacity { get; }

    void SetSkin(string skinKey);

    void SetOpacity(double opacity);

    /// <summary>只改这个窗口的整窗透明度。</summary>
    void SetWindowOpacity(double opacity);

    /// <summary>取消单独设置，回到跟随全局。</summary>
    void ResetWindowOpacity();

    void RefreshAppearance();
}
