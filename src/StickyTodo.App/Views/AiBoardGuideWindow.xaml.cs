using System.Windows;
using System.Windows.Input;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Views;

/// <summary>
/// 「AI 看板」引导窗：管理面板点「+ AI 看板」不再直接建一块空看板。
///
/// AI 看板的内容（HTML + 取数脚本）本来就是 AI 通过 MCP 写的，面板这边能做的是把
/// 「怎么让 AI 帮你做一块」讲清楚：开 MCP → 把接入配置交给 AI → 说一句想要什么，
/// 示例话术可以一键复制。左边那条路走不通（不想配 MCP）时，还能直接去看板市场装一个现成的。
/// </summary>
public partial class AiBoardGuideWindow : Window
{
    public AiBoardGuideWindow()
    {
        InitializeComponent();

        // 只为了 {Binding Converter={StaticResource Loc}, …}：翻译器只认 ConverterParameter，
        // 绑定源是谁无所谓，但总得有个 DataContext，否则转换器跑不起来。
        DataContext = this;

        Loaded += (_, _) => OkButton.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    /// <summary>把示例话术抄到剪贴板（剪贴板被别的进程占着时重试几次，失败只记日志）。</summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCopy(Loc.T("manager.aiGuideSample")))
        {
            Serilog.Log.Warning("[ai-board] 示例话术复制失败：剪贴板被其他进程占用");
        }
    }

    private static bool TryCopy(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                System.Threading.Thread.Sleep(30);
            }
        }

        return false;
    }

    /// <summary>
    /// 点「逛看板市场」时调谁：管理面板把它自己的开窗方法交进来。
    /// 有这个回调就不再靠 <see cref="Window.Owner"/>——没有 Owner（居中弹出）时市场照样能开。
    /// </summary>
    internal Action? OpenMarket { get; set; }

    /// <summary>去看板市场：先把引导窗收掉，市场交给管理面板去开（单例和 owner 都在它手上）。</summary>
    private void Market_Click(object sender, RoutedEventArgs e)
    {
        var manager = Owner as ManagerWindow;
        var openMarket = OpenMarket;

        Close();

        if (openMarket is not null)
        {
            openMarket();
            return;
        }

        manager?.OpenMarketWindow();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
