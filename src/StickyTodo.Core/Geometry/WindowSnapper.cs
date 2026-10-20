namespace StickyTodo.Core.Geometry;

/// <summary>
/// 贴边吸附：把窗口的左/上坐标吸附到屏幕工作区边缘、或其它便签窗口的边。
/// 纯函数实现，不依赖 WPF，因此可以直接做单元测试。
/// </summary>
public static class WindowSnapper
{
    public const double DefaultThreshold = 10d;

    /// <summary>
    /// 返回吸附后的矩形。X、Y 两个方向独立判断：各自取距离最近、且不超过阈值的候选值。
    /// </summary>
    public static SnapRect Snap(
        SnapRect moving,
        SnapRect workArea,
        IReadOnlyList<SnapRect> others,
        double threshold = DefaultThreshold)
    {
        return new SnapRect(
            SnapAxis(moving.Left, moving.Width, workArea.Left, workArea.Right, others.Select(o => (o.Left, o.Right)), threshold),
            SnapAxis(moving.Top, moving.Height, workArea.Top, workArea.Bottom, others.Select(o => (o.Top, o.Bottom)), threshold),
            moving.Width,
            moving.Height);
    }

    private static double SnapAxis(
        double position,
        double size,
        double areaStart,
        double areaEnd,
        IEnumerable<(double Start, double End)> others,
        double threshold)
    {
        var best = position;
        var bestDistance = double.MaxValue;

        void Consider(double candidate)
        {
            var distance = Math.Abs(candidate - position);
            if (distance > threshold || distance >= bestDistance)
            {
                return;
            }

            bestDistance = distance;
            best = candidate;
        }

        // 屏幕工作区：贴左/上边、贴右/下边
        Consider(areaStart);
        Consider(areaEnd - size);

        // 其它窗口：左右边对齐、以及紧贴对方的边
        foreach (var (start, end) in others)
        {
            Consider(start - size); // 我的右边贴对方左边
            Consider(end);          // 我的左边贴对方右边
            Consider(start);        // 左边对齐
            Consider(end - size);   // 右边对齐
        }

        return best;
    }
}