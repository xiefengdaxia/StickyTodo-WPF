using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>桌面悬浮时钟：小窗、置顶、不进任务栏，悬浮在桌面右下角。</summary>
public partial class DesktopClockBoardWindow : Window
{
    private readonly DesktopClockViewModel _viewModel;

    public DesktopClockBoardWindow(DesktopClockViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // 点天气/日历要把看板贴到这个窗口旁边，所以得知道自己是谁
        viewModel.AttachWindow(this);

        // 首次布局后挪到右下角并嵌入桌面层；启动时钟与天气轮播
        Opened += (_, _) =>
        {
            MoveToBottomRight();
            EmbedIntoDesktop();
            viewModel.Start();
        };

        // SizeToContent 会随内容（天气轮播文案）多次改变高度：每次变了都重新贴右下角，
        // 直到用户自己拖动过为止
        SizeChanged += (_, _) => MoveToBottomRight();

        // 点一下会被系统抬到普通窗口带顶部：失焦后重新沉回桌面层
        Deactivated += (_, _) => EmbedIntoDesktop();

        Closed += (_, _) => viewModel.Stop();
    }

    /// <summary>
    /// 挂到桌面层；挂不上（没有 WorkerW 桌面层 / 句柄异常）就退回置顶，
    /// 否则窗口沉到最底下用户会以为"时钟没了"。
    /// </summary>
    private void EmbedIntoDesktop()
    {
        // 顺序很重要：先脱离置顶带，再插到桌面层后面。
        // 反过来做的话，取消置顶的那次 SetWindowPos 会把窗口抬回非置顶带顶部。
        if (Topmost)
        {
            Topmost = false;
        }

        if (!DesktopEmbed.Embed(this))
        {
            Topmost = true;
        }
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    private int _placeTries;

    /// <summary>用户手动拖过之后不再自动贴角。</summary>
    private bool _userMoved;

    private void MoveToBottomRight()
    {
        if (_userMoved)
        {
            return;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scale = screen?.Scaling is { } s and > 0 ? s : 1;
        var work = screen?.WorkingArea;
        if (work is null)
        {
            return;
        }

        // Bounds 是 DIP，WorkingArea 是物理像素
        var w = Bounds.Width * scale;
        var h = Bounds.Height * scale;

        // SizeToContent 可能还没算完（此时高度仍是初始大值）：等下一帧再看，
        // 否则窗口会停在屏幕中间而不是右下角
        if ((h <= 0 || h > work.Value.Height - 40) && _placeTries++ < 10)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(MoveToBottomRight, Avalonia.Threading.DispatcherPriority.Loaded);
            return;
        }

        Position = new Avalonia.PixelPoint(
            (int)Math.Round(work.Value.X + work.Value.Width - w - 24),
            (int)Math.Round(work.Value.Y + work.Value.Height - h - 24));
    }

    private void Root_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 按下先"待定"：移动超过阈值才算拖动，否则松开时按区域触发（点天气/日程/时间…）
        _pressed = true;
        _dragging = false;
        _pressPosition = Position;
        _grabX = ScreenPointerX(e);
        _grabY = ScreenPointerY(e);
        _pressedTag = FindTag(e.Source as Avalonia.Visual);
        e.Pointer.Capture(sender as Avalonia.Input.IInputElement);
        e.Handled = true;
    }

    private const double DragThreshold = 4;   // DIP

    private bool _pressed;
    private bool _dragging;
    private Avalonia.PixelPoint _pressPosition;
    private double _grabX;
    private double _grabY;
    private string? _pressedTag;

    /// <summary>缩放：Position 是物理像素，指针坐标是 DIP，换算要用它。</summary>
    private double Scale => Screens.ScreenFromWindow(this)?.Scaling is { } s and > 0 ? s : 1.0;

    // 指针的屏幕坐标（DIP）= 窗口屏幕位置 / 缩放 + 指针在窗口内的位置。
    // 拖动过程中窗口在动，但这两项互相抵消，得到的始终是指针的真实屏幕位置。
    private double ScreenPointerX(Avalonia.Input.PointerEventArgs e) => Position.X / Scale + e.GetPosition(this).X;

    private double ScreenPointerY(Avalonia.Input.PointerEventArgs e) => Position.Y / Scale + e.GetPosition(this).Y;

    private void Root_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var dx = ScreenPointerX(e) - _grabX;
        var dy = ScreenPointerY(e) - _grabY;

        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            _userMoved = true;   // 真的拖了才关掉自动贴角

            // 拖动期间先摘掉阴影：透明窗口每次移动都要重绘阴影，很容易掉帧发飘
            _parkedShadow = NoteRoot.BoxShadow;
            NoteRoot.BoxShadow = default;
        }

        // 不足 1 个物理像素的位移不移动窗口（亚像素级的连续 SetWindowPos 正是抖动的来源）
        var nextX = (int)Math.Round(_pressPosition.X + (dx * Scale));
        var nextY = (int)Math.Round(_pressPosition.Y + (dy * Scale));
        if (nextX == Position.X && nextY == Position.Y)
        {
            return;
        }

        Position = new Avalonia.PixelPoint(nextX, nextY);
    }

    private Avalonia.Media.BoxShadows _parkedShadow;

    private void EndFastDrag()
    {
        NoteRoot.BoxShadow = _parkedShadow;
    }

    private void Root_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        e.Pointer.Capture(null);

        // 没拖动 = 单击：按按下时所在的区域触发
        if (!_dragging)
        {
            Activate(_pressedTag);
        }
        else
        {
            EndFastDrag();
        }

        _dragging = false;
        _pressedTag = null;
    }

    /// <summary>单击区域 → 对应看板（开着就关）。</summary>
    private void Activate(string? tag)
    {
        switch (tag)
        {
            case "calendar":
                _viewModel.ToggleCalendarBoard();
                break;
            case "clock":
                _viewModel.ToggleWorldClockBoard();
                break;
            case "weather":
                _viewModel.ToggleWeatherBoard();
                break;
            case "todo":
                _viewModel.ToggleAgendaBoard();
                break;
            case "token":
                _viewModel.ToggleTokenBoard();
                break;
        }
    }

    /// <summary>从被点中的元素往上找第一个带字符串 Tag 的元素。</summary>
    private static string? FindTag(Avalonia.Visual? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Avalonia.Controls.Control { Tag: string tag })
            {
                return tag;
            }

            current = current.GetVisualParent();
        }

        return null;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
