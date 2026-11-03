using System.Windows;
using System.Windows.Input;

namespace StickyTodo.App.Views;

/// <summary>
/// 跟随皮肤的确认框 / 提示框，替代系统 <c>MessageBox</c>：
/// 系统对话框永远是白底 Win32 样式，和便签的纸面观感割裂，暗色皮肤下尤其突兀。
///
/// 用法与 MessageBox 一样是「同步返回」，调用点不用改成 async：
/// <code>
/// if (ConfirmWindow.Confirm(this, "确认删除", "删除标签「工作」？")) { ... }
/// ConfirmWindow.Notify(this, "规则无效", "这条规则看不懂：FREQ=XXX");
/// </code>
/// </summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow(string heading, string body, string okText, bool showCancel)
    {
        InitializeComponent();

        Title = heading;
        HeadingText.Text = heading;
        BodyText.Text = body;

        if (!string.IsNullOrWhiteSpace(okText))
        {
            OkButton.Content = okText;
        }

        if (!showCancel)
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => OkButton.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                // 只有「确定」的提示框，Esc 等同于点确定
                DialogResult = !showCancel;
                e.Handled = true;
            }
        };
    }

    /// <summary>确认框：返回 true 表示用户点了「确定」。</summary>
    public static bool Confirm(Window? owner, string heading, string body, string? okText = null) =>
        Run(owner, heading, body, okText, showCancel: true);

    /// <summary>提示框：只有一个「确定」，纯告知。</summary>
    public static void Notify(Window? owner, string heading, string body, string? okText = null) =>
        Run(owner, heading, body, okText, showCancel: false);

    private static bool Run(Window? owner, string heading, string body, string? okText, bool showCancel)
    {
        var window = new ConfirmWindow(heading, body, okText ?? string.Empty, showCancel);

        // 主窗口还没建出来（比如启动失败）时退回无 owner，ShowDialog 不要求 owner
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window.ShowDialog() == true;
    }

    /// <summary>回归用：只建窗口、不进入模态循环（截图脚本用）。</summary>
    internal static ConfirmWindow CreatePreview(string heading, string body, string? okText = null) =>
        new(heading, body, okText ?? string.Empty, showCancel: true);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

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
