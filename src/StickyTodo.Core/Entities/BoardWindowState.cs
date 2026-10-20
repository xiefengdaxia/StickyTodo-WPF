namespace StickyTodo.Core.Entities;

/// <summary>
/// 看板窗口几何位置（WPF 设备无关单位）。
/// 用于重启后恢复用户把窗口拖到哪、拉成多大。
/// </summary>
public readonly record struct BoardWindowState(double Left, double Top, double Width, double Height)
{
    public bool IsValid =>
        Width >= 240 &&
        Height >= 200 &&
        !double.IsNaN(Left) && !double.IsNaN(Top) &&
        !double.IsInfinity(Left) && !double.IsInfinity(Top) &&
        !double.IsNaN(Width) && !double.IsNaN(Height) &&
        !double.IsInfinity(Width) && !double.IsInfinity(Height);
}