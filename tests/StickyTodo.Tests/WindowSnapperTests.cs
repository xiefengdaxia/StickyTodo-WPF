using StickyTodo.Core.Geometry;

namespace StickyTodo.Tests;

public sealed class WindowSnapperTests
{
    private static readonly SnapRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void SnapsToScreenLeftEdge_WhenClose()
    {
        var result = WindowSnapper.Snap(new SnapRect(6, 300, 400, 500), WorkArea, []);
        Assert.Equal(0, result.Left);
        Assert.Equal(300, result.Top);
    }

    [Fact]
    public void SnapsToScreenRightEdge_WhenClose()
    {
        // 1920 - 400 = 1520，当前 1514，差 6 < 阈值
        var result = WindowSnapper.Snap(new SnapRect(1514, 300, 400, 500), WorkArea, []);
        Assert.Equal(1520, result.Left);
    }

    [Fact]
    public void DoesNotSnap_WhenBeyondThreshold()
    {
        var moving = new SnapRect(40, 300, 400, 500);
        var result = WindowSnapper.Snap(moving, WorkArea, []);
        Assert.Equal(moving.Left, result.Left);
        Assert.Equal(moving.Top, result.Top);
    }

    [Fact]
    public void SnapsToOtherWindow_RightEdgeToLeftEdge()
    {
        var other = new SnapRect(600, 100, 400, 500);
        // 我的右边在 597，对方左边 600，差 3
        var result = WindowSnapper.Snap(new SnapRect(200, 100, 397, 500), WorkArea, [other]);
        Assert.Equal(203, result.Left);      // 左边被推到 203，使右边缘正好贴合
        Assert.Equal(600, result.Right);
    }

    [Fact]
    public void SnapsToOtherWindow_LeftEdgesAlign()
    {
        var other = new SnapRect(600, 100, 400, 500);
        var result = WindowSnapper.Snap(new SnapRect(606, 700, 300, 200), WorkArea, [other]);
        Assert.Equal(600, result.Left);
    }

    [Fact]
    public void SnapsToOtherWindow_TopEdgeBelowOther()
    {
        var other = new SnapRect(100, 100, 400, 500); // bottom = 600
        var result = WindowSnapper.Snap(new SnapRect(100, 604, 300, 200), WorkArea, [other]);
        Assert.Equal(600, result.Top);
    }

    [Fact]
    public void PicksNearestCandidate_WhenBothScreenAndWindowAreInRange()
    {
        // 屏幕左边 0（差 8）、对方左边 5（差 3）→ 应吸到 5
        var other = new SnapRect(5, 800, 100, 100);
        var result = WindowSnapper.Snap(new SnapRect(8, 900, 300, 200), WorkArea, [other]);
        Assert.Equal(5, result.Left);
    }

    [Fact]
    public void KeepsWidthAndHeight()
    {
        var result = WindowSnapper.Snap(new SnapRect(3, 3, 321, 456), WorkArea, []);
        Assert.Equal(321, result.Width);
        Assert.Equal(456, result.Height);
    }
}