using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StickyTodo.App.Services;

namespace StickyTodo.App.Views;

/// <summary>
/// 锁定看板时浮在"锁定按钮"位置的小解锁徽标。
///
/// 看板窗口本身被设成鼠标穿透（点到什么都是下面那层），这个徽标是独立窗口、
/// 盖在锁定图标上，所以只有它还能点——点一下解锁并自动消失。
/// </summary>
public partial class UnlockChipWindow : Window
{
    private readonly Window _board;
    private readonly FrameworkElement _anchor;
    private readonly Action _unlock;

    public UnlockChipWindow(Window board, FrameworkElement anchor, Action unlock)
    {
        _board = board;
        _anchor = anchor;
        _unlock = unlock;

        InitializeComponent();

Owner = board;
Loaded += (_, _) =>
{
    // 不置顶，但要压在看板之上：只在"看板露在桌面/前面"时可见可点
    ClickThrough.HideFromAltTab(this);
    ClickThrough.PlaceAbove(this, board);
    Follow();
};
board.Activated += OnBoardActivated;
        board.LocationChanged += OnBoardGeometryChanged;
        board.SizeChanged += OnBoardGeometryChanged;
        board.Closed += OnBoardClosed;
    }

    private void OnBoardGeometryChanged(object? sender, EventArgs e) => Follow();

    /// <summary>看板被激活（可能盖过徽标）时，把徽标重新摆到它上面。</summary>
    private void OnBoardActivated(object? sender, EventArgs e)
    {
        ClickThrough.PlaceAbove(this, _board);
        Follow();
    }

    private void OnBoardClosed(object? sender, EventArgs e) => Close();

    /// <summary>把徽标摆到锁定按钮正中。</summary>
    private void Follow()
    {
        if (!_board.IsVisible || _anchor.ActualWidth < 1)
        {
            return;
        }

        var physical = ClickThrough.GetScreenRect(_anchor);
        if (physical.IsEmpty)
        {
            return;
        }

        var dips = ClickThrough.ToDips(_anchor, physical);
        Left = dips.Left + ((dips.Width - Width) / 2);
        Top = dips.Top + ((dips.Height - Height) / 2);
    }

    private void Chip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TriggerUnlock();
    }

    /// <summary>解锁（点击徽标走这里；回归脚本也能直接调）。</summary>
    public void TriggerUnlock()
    {
        _unlock();
        Close();
    }

    /// <summary>回归断言用：徽标当前是否置顶（本版刻意不置顶）。</summary>
    public bool IsTopmostChip => Topmost;

    /// <summary>看板布局变化后手动重定位（首次显示时调用）。</summary>
    public void FollowAnchor() => Follow();

    protected override void OnClosed(EventArgs e)
    {
        _board.LocationChanged -= OnBoardGeometryChanged;
        _board.SizeChanged -= OnBoardGeometryChanged;
        _board.Activated -= OnBoardActivated;
        _board.Closed -= OnBoardClosed;
        base.OnClosed(e);
    }
}