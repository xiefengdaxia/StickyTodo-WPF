using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StickyTodo.App.Views;

/// <summary>
/// 录制范围的红框：只在区域外面（或整屏时贴着边缘内侧）画 2~3px 的线，
/// 鼠标穿透、不抢焦点、不进 Alt-Tab，并尽量从系统截屏里排除掉。
/// </summary>
public partial class GifFrameWindow : Window
{
    private readonly Rectangle _region;

    public GifFrameWindow(Rectangle regionPx)
    {
        InitializeComponent();
        _region = regionPx;

        Loaded += (_, _) => ApplyBounds();
    }

    private void ApplyBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var screen = Services.ScreenCaptureService.GetVirtualScreenBounds();

        // 有位置就把线画在区域外面（这样 GDI 抓屏抓不到它）；
        // 整屏录制时外面没地方，只能贴边内侧。
        var outside = new Rectangle(_region.X - 2, _region.Y - 2, _region.Width + 4, _region.Height + 4);
        var fitsOutside = outside.Left >= screen.Left && outside.Top >= screen.Top
                          && outside.Right <= screen.Right && outside.Bottom <= screen.Bottom;

        var rect = fitsOutside ? outside : _region;
        Frame.BorderThickness = new Thickness(fitsOutside ? 2 : 3);
        Frame.Margin = new Thickness(0);

        _ = SetWindowPos(handle, HwndTopmost, rect.X, rect.Y, rect.Width, rect.Height,
            SwpNoActivate | SwpShowWindow);

        var ex = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, ex | WsExTransparent | WsExNoactivate | WsExToolwindow);
        _ = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBounds();
    }

    private static readonly IntPtr HwndTopmost = new(-1);

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
