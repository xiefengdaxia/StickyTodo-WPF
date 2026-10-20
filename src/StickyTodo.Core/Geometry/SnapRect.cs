namespace StickyTodo.Core.Geometry;

/// <summary>与 UI 框架无关的矩形，方便对吸附算法做纯单元测试。</summary>
public readonly record struct SnapRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}