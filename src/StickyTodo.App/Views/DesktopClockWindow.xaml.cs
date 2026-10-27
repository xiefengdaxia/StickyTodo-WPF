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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // 点在按钮上不拖动
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

        try
        {
            DragMove();

            // 拖完就关掉"自动贴角"，否则尺寸一变又被拉回右下角
            _userMoved = true;
        }
        catch (InvalidOperationException)
        {
            // 鼠标可能已经抬起
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
