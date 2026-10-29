using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;


namespace StickyTodo.App.Views;

/// <summary>桌面悬浮时钟窗口：阳历 + 农历 + 时间，天气就绪时轮播天气。</summary>
public partial class DesktopClockWindow : Window
{
    private readonly DesktopClockViewModel _viewModel;

    public DesktopClockWindow(DesktopClockViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // 点天气/日历要把看板贴到这个窗口旁边，所以得知道自己是谁
        viewModel.AttachWindow(this);

        // ActualWidth 要等布局完成才有值，所以首帧渲染后再挪位 + 嵌入桌面层
        ContentRendered += (_, _) =>
        {
            MoveToBottomRight();
            EmbedIntoDesktop();
        };
        Loaded += (_, _) => viewModel.Start();

        // SizeToContent 会随内容（天气轮播文案）改变高度：每次变了都重新贴右下角，
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
        // 先脱离置顶带，再插到桌面层后面；反过来会被 SetWindowPos 抬回非置顶带顶部
        if (Topmost)
        {
            Topmost = false;
        }

        if (!DesktopEmbed.Embed(this))
        {
            Topmost = true;
        }
    }

    private void MoveToBottomRight()
    {
        if (_userMoved)
        {
            return;
        }

        var screen = System.Windows.SystemParameters.WorkArea;
        Left = screen.Right - ActualWidth - 24;
        Top = screen.Bottom - ActualHeight - 24;
    }

    /// <summary>用户手动拖过之后不再自动贴角。</summary>
    private bool _userMoved;

    /// <summary>回归用：把挂件当前内容收成一行日志。</summary>
    internal string DescribeForTest() =>
        $"农历={_viewModel.LunarText} | 天气={_viewModel.WeatherLine} | 天气细节={_viewModel.WeatherDetail} " +
        $"| 日程={_viewModel.AgendaLine} | 日程细节={_viewModel.AgendaDetail}";

    /// <summary>回归用：当前取数用的城市（验证"改默认城市后立刻换"）。</summary>
    internal string LoadedCityForTest => _viewModel.LoadedCityForTest;

    /// <summary>回归用：模拟点"今日日程"。</summary>
    internal void OpenCalendarForTest() => _viewModel.ToggleCalendarBoard();

    /// <summary>回归用：模拟点天气行。</summary>
    internal void OpenWeatherForTest() => _viewModel.ToggleWeatherBoard();

    /// <summary>回归用：模拟点某个区域（calendar / clock / weather / todo / token）。</summary>
    internal void ClickAreaForTest(string tag) => Activate(tag);

    // ---------- 单击 / 拖动：按下先"待定"，移动超过阈值才算拖，否则松开时按区域触发 ----------

    private const double DragThreshold = 4;   // DIP

    private bool _pressed;
    private bool _dragging;
    private Point _lastPoint;
    private string? _pressedTag;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // 点在按钮上不处理
        if (e.OriginalSource is DependencyObject source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is System.Windows.Controls.Button)
                {
                    return;
                }

                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
        }

        _pressed = true;
        _dragging = false;
        _lastPoint = e.GetPosition(this);
        _pressedTag = FindTag(e.OriginalSource as DependencyObject);
        (sender as UIElement)?.CaptureMouse();
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var now = e.GetPosition(this);

        if (!_dragging)
        {
            if (Math.Abs(now.X - _lastPoint.X) < DragThreshold && Math.Abs(now.Y - _lastPoint.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            _userMoved = true;   // 真的拖了才关掉自动贴角
        }

        // 用当前 Left/Top 累加本帧位移：e.GetPosition 是相对窗口的，窗口移动后它也跟着变，
        // 用按下时的 Left 做基准会导致每帧只修正一半误差 → 抖动
        Left += now.X - _lastPoint.X;
        Top += now.Y - _lastPoint.Y;
        _lastPoint = now;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        (sender as UIElement)?.ReleaseMouseCapture();

        // 没拖动 = 单击：按按下时所在的区域触发
        if (!_dragging)
        {
            Activate(_pressedTag);
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
    private static string? FindTag(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { Tag: string tag })
            {
                return tag;
            }

            current = current is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(current) : null;
        }

        return null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
