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

        try
        {
            BeginMoveDrag(e);

            // 拖完就把"自动贴角"关掉，否则尺寸一变又被拉回右下角
            _userMoved = true;
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
