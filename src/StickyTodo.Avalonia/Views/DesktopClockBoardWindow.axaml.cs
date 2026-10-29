using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

        // 手动拖动：BeginMoveDrag() 走的是 WM_NCLBUTTONDOWN 的系统移动循环，
        // 时钟这种"无边框 + 被压到桌面层"的窗口经常直接不生效（表现就是"拖不动"）。
        _dragging = true;
        _userMoved = true;                    // 用户接手后不再自动贴角
        _pressPosition = Position;
        _grabX = ScreenPointerX(e);
        _grabY = ScreenPointerY(e);
        e.Pointer.Capture(sender as Avalonia.Input.IInputElement);
        e.Handled = true;
    }

    private bool _dragging;
    private Avalonia.PixelPoint _pressPosition;
    private double _grabX;
    private double _grabY;

    /// <summary>缩放：Position 是物理像素，指针坐标是 DIP，换算要用它。</summary>
    private double Scale => Screens.ScreenFromWindow(this)?.Scaling is { } s and > 0 ? s : 1.0;

    // 指针的屏幕坐标（DIP）= 窗口屏幕位置 / 缩放 + 指针在窗口内的位置。
    // 拖动过程中窗口在动，但这两项互相抵消，得到的始终是指针的真实屏幕位置。
    private double ScreenPointerX(Avalonia.Input.PointerEventArgs e) => Position.X / Scale + e.GetPosition(this).X;

    private double ScreenPointerY(Avalonia.Input.PointerEventArgs e) => Position.Y / Scale + e.GetPosition(this).Y;

    private void Root_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var dx = ScreenPointerX(e) - _grabX;
        var dy = ScreenPointerY(e) - _grabY;

        Position = new Avalonia.PixelPoint(
            (int)Math.Round(_pressPosition.X + (dx * Scale)),
            (int)Math.Round(_pressPosition.Y + (dy * Scale)));
    }

    private void Root_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ---------- 联动：点天气行打开天气看板、点日程行打开日历 ----------
    // 按下时就把事件吃掉，否则会被外层的"拖动窗口"处理器接走（BeginMoveDrag 会抢指针）。

    // 日期/农历那片：点一下打开日历
    private void CalendarArea_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
        }
    }

    private void CalendarArea_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left)
        {
            return;
        }

        e.Handled = true;
        _viewModel.ToggleCalendarBoard();
    }

    private void WeatherRow_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
        }
    }

    private void WeatherRow_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left)
        {
            return;
        }

        e.Handled = true;
        _viewModel.ToggleWeatherBoard();
    }

    private void AgendaRow_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
        }
    }

    private void AgendaRow_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left)
        {
            return;
        }

        e.Handled = true;
        _viewModel.ToggleCalendarBoard();
    }
}
