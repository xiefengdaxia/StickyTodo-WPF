using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace StickyTodo.Notes.Services;

/// <summary>
/// 应用图标：窗口用 <see cref="WindowIcon"/>，托盘也是同一个 ICO。
/// 都从嵌入资源读取，保证处处一致。
/// </summary>
public static class AppIcon
{
    private const string ResourcePath = "avares://StickyTodo.Avalonia/Assets/stickytodo.ico";

    private static WindowIcon? _icon;

    /// <summary>窗口/托盘图标。读取失败时返回 null，不影响启动。</summary>
    public static WindowIcon? Icon
    {
        get
        {
            if (_icon is not null)
            {
                return _icon;
            }

            try
            {
                using var stream = AssetLoader.Open(new Uri(ResourcePath));
                _icon = new WindowIcon(stream);
                return _icon;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
