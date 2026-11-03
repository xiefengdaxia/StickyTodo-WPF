using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StickyTodo.Notes.Services;

namespace StickyTodo.Notes.Views;

/// <summary>
/// 锁定看板时浮在"锁定按钮"位置的小解锁徽标（Avalonia 版）。
/// 看板窗口本身是鼠标穿透的，这个独立小窗口盖在锁定图标上，是唯一还能点的地方。
/// </summary>
public partial class UnlockChipWindow : Window
{
    private readonly Window _board;
    private readonly Control _anchor;
    private readonly Action _unlock;

    public UnlockChipWindow(Window board, Control anchor, Action unlock)
    {
        _board = board;
        _anchor = anchor;
        _unlock = unlock;

        InitializeComponent();
        Chrome.Apply(this, Chip, 13, 0);

        board.PositionChanged += OnGeometryChanged;
        board.SizeChanged += OnGeometryChanged;
        board.Closed += OnBoardClosed;
        Opened += (_, _) =>
        {
            ClickThrough.HideFromAltTab(this);
            ClickThrough.PlaceAbove(this, board);
            Follow();
        };
        board.Activated += OnBoardActivated;
    }

    /// <summary>把徽标摆到锁定按钮正中。</summary>
    public void Follow()
    {
        var rect = ClickThrough.GetScreenRect(_anchor);
        if (rect is not { } r || r.Width < 1)
        {
            return;
        }

        var w = (int)Math.Round(Bounds.Width * (RenderScaling <= 0 ? 1 : RenderScaling));
        var h = (int)Math.Round(Bounds.Height * (RenderScaling <= 0 ? 1 : RenderScaling));

        Position = new PixelPoint(
            r.X + ((r.Width - w) / 2),
            r.Y + ((r.Height - h) / 2));
    }

private void OnGeometryChanged(object? sender, EventArgs e) => Follow();

    /// <summary>看板被激活（可能盖过徽标）时，把徽标重新摆到它上面。</summary>
    private void OnBoardActivated(object? sender, EventArgs e)
    {
        ClickThrough.PlaceAbove(this, _board);
        Follow();
    }

    private void OnBoardClosed(object? sender, EventArgs e) => Close();

    private void Chip_Click(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;
        _unlock();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _board.PositionChanged -= OnGeometryChanged;
        _board.SizeChanged -= OnGeometryChanged;
        _board.Activated -= OnBoardActivated;
        _board.Closed -= OnBoardClosed;
        base.OnClosed(e);
    }
}