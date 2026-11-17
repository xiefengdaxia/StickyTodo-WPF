using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using Serilog;
using WinForms = System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>浮层想贴的那一侧（相对被操作的区域）。</summary>
public enum OverlaySide
{
    Above,
    Below,
    Left,
    Right,
}

/// <summary>
/// 截图 / 录屏浮层的统一摆位。
///
/// 坐标系一律用**屏幕物理像素**（录制区域、GDI 抓屏、<c>Screen.WorkingArea</c> 都是这个坐标系），
/// 只在最后写 <see cref="Window.Left"/> / <see cref="Window.Top"/> 时按目标显示器的 DPI 折成 DIP。
///
/// 以前的写法直接拿「整个虚拟屏」当边界，于是区域贴着右侧/底部边缘时——
/// 尤其还压着任务栏——浮层会被摆到可见区域之外。这里改成：
/// 以**区域所在那块屏的工作区**为界，首选位置放不下就翻到左侧 / 上方，
/// 四周都放不下（整屏录制）再退到区域内侧，最后兜底夹进工作区。
/// </summary>
public static class OverlayPlacement
{
    /// <summary>浮层与区域 / 屏幕边缘之间的默认间距（物理像素）。</summary>
    public const double DefaultMargin = 8;

    /// <summary>
    /// 把浮层摆到 <paramref name="anchorPx"/>（区域，物理像素）旁边：
    /// 首选 <paramref name="preferred"/> 那一侧的外侧；被屏幕边缘挤掉就依次试其余三边；
    /// 四周都没地方（区域几乎占满整屏）就退到区域内侧的左上、再是左下；
    /// 最后兜底：按首选位置夹进工作区，至少保证整块可见。
    /// </summary>
    public static void PlaceNear(
        Window window,
        Rectangle anchorPx,
        OverlaySide preferred = OverlaySide.Above,
        double margin = DefaultMargin)
    {
        var size = MeasurePx(window, anchorPx);

        foreach (var candidate in Candidates(anchorPx, size, preferred, margin))
        {
            if (WorkAreaFor(candidate).Contains(candidate))
            {
                MoveTo(window, candidate, anchorPx);
                return;
            }
        }

        ClampToWorkArea(window, OutsideRect(anchorPx, size, preferred, margin));
    }

    /// <summary>
    /// 把浮层夹进它所在那块屏的工作区：拿 <paramref name="preferredPx"/> 的左上角当期望位置，
    /// 尺寸用窗口自己的实际尺寸。右下角的提示条、贴在边缘的小徽标都走这里。
    /// </summary>
    public static void ClampToWorkArea(Window window, Rectangle preferredPx, double margin = DefaultMargin)
    {
        var work = WorkAreaFor(preferredPx);
        var size = MeasurePx(window, work);

        var m = (int)Math.Round(Math.Max(0, margin));
        var maxLeft = Math.Max(work.Left, work.Right - size.Width - m);
        var maxTop = Math.Max(work.Top, work.Bottom - size.Height - m);

        var left = Math.Min(Math.Max(preferredPx.Left, work.Left + m), maxLeft);
        var top = Math.Min(Math.Max(preferredPx.Top, work.Top + m), maxTop);

        MoveTo(window, new Rectangle(left, top, size.Width, size.Height), work);
    }

    /// <summary>
    /// 浮层的实际尺寸（物理像素）：窗口是 DIP，乘上目标屏的 DPI 缩放。
    /// 还没布局好时退回 XAML 里写的 Width/Height，保证算出来的位置不是 0×0。
    /// </summary>
    public static System.Drawing.Size MeasurePx(Window window, Rectangle nearPx)
    {
        window.UpdateLayout();

        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        if (double.IsNaN(width) || width <= 0)
        {
            width = 160;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = 40;
        }

        var scale = ScaleFor(nearPx);
        return new System.Drawing.Size(
            Math.Max(1, (int)Math.Ceiling(width * scale)),
            Math.Max(1, (int)Math.Ceiling(height * scale)));
    }

    /// <summary>按物理像素矩形摆窗口（内部按目标屏 DPI 折成 DIP 写 Left/Top）。</summary>
    public static void MoveTo(Window window, Rectangle targetPx, Rectangle nearPx)
    {
        var scale = ScaleFor(nearPx);
        window.Left = targetPx.Left / scale;
        window.Top = targetPx.Top / scale;

        Log.Debug("[overlay] {Window} 摆到 {X},{Y}（{W}x{H} 物理像素 · 缩放 {Scale:0.##}）",
            window.GetType().Name, targetPx.Left, targetPx.Top,
            targetPx.Width, targetPx.Height, scale);
    }

    /// <summary>
    /// <paramref name="px"/>（物理像素矩形）所在显示器的工作区。
    /// 多屏时按「交叠面积最大」挑，跨屏 / 离屏就取最近的那块；都拿不到才退回整个虚拟屏。
    /// </summary>
    public static Rectangle WorkAreaFor(Rectangle px)
    {
        try
        {
            Rectangle? best = null;
            var bestArea = 0L;

            foreach (var screen in WinForms.Screen.AllScreens)
            {
                var area = screen.WorkingArea;
                if (area.Width <= 0 || area.Height <= 0)
                {
                    continue;
                }

                var overlap = Rectangle.Intersect(area, px);
                var overlapArea = (long)overlap.Width * overlap.Height;
                if (overlapArea > bestArea)
                {
                    bestArea = overlapArea;
                    best = area;
                }
            }

            if (best is { } hit)
            {
                return hit;
            }

            // 一点都没交叠（区域坐标越界 / 刚换过分辨率）：取离它最近的那块屏
            var nearest = WinForms.Screen.FromPoint(
                new System.Drawing.Point(px.Left + (px.Width / 2), px.Top + (px.Height / 2)));
            if (nearest is not null)
            {
                var area = nearest.WorkingArea;
                if (area.Width > 0 && area.Height > 0)
                {
                    return area;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[overlay] 取显示器工作区失败，退回整个虚拟屏");
        }

        return ScreenCaptureService.GetVirtualScreenBounds();
    }

    /// <summary>候选位置：首选那一侧 → 其余三边 → 区域内侧左上 / 左下。</summary>
    private static IEnumerable<Rectangle> Candidates(Rectangle anchor, System.Drawing.Size size, OverlaySide preferred, double margin)
    {
        foreach (var side in Order(preferred))
        {
            yield return OutsideRect(anchor, size, side, margin);
        }

        var m = (int)Math.Round(Math.Max(0, margin));

        // 区域贴满屏（整屏录制）时四周都没有空位：缩进区域内侧，
        // 这时工具条会被录进去一点——靠 WDA_EXCLUDEFROMCAPTURE 兜着，
        // 但「看得见、点得到」比「完全不入镜」更要紧。
        yield return new Rectangle(anchor.Left + m, anchor.Top + m, size.Width, size.Height);
        yield return new Rectangle(anchor.Left + m, anchor.Bottom - size.Height - m, size.Width, size.Height);
    }

    private static IEnumerable<OverlaySide> Order(OverlaySide preferred)
    {
        yield return preferred;

        foreach (var side in new[] { OverlaySide.Above, OverlaySide.Below, OverlaySide.Left, OverlaySide.Right })
        {
            if (side != preferred)
            {
                yield return side;
            }
        }
    }

    /// <summary>区域某一侧的「外侧」位置。</summary>
    private static Rectangle OutsideRect(Rectangle anchor, System.Drawing.Size size, OverlaySide side, double margin)
    {
        var m = (int)Math.Round(Math.Max(0, margin));

        return side switch
        {
            OverlaySide.Above => new Rectangle(anchor.Left, anchor.Top - size.Height - m, size.Width, size.Height),
            OverlaySide.Below => new Rectangle(anchor.Left, anchor.Bottom + m, size.Width, size.Height),
            OverlaySide.Left => new Rectangle(anchor.Left - size.Width - m, anchor.Top, size.Width, size.Height),
            _ => new Rectangle(anchor.Right + m, anchor.Top, size.Width, size.Height),
        };
    }

    /// <summary>目标矩形所在显示器的 DPI 缩放（1.0 = 96dpi）。</summary>
    private static double ScaleFor(Rectangle px)
    {
        var scale = GetScaleForPoint(new NativePoint(px.Left + (px.Width / 2), px.Top + (px.Height / 2)));

        if (scale <= 0.1)
        {
            scale = 1.0;
        }

        return scale;
    }

    private static double GetScaleForPoint(NativePoint pxPoint)
    {
        try
        {
            var monitor = MonitorFromPoint(pxPoint, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[overlay] GetDpiForMonitor 不可用，退回 96dpi");
        }

        return 1.0;
    }

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdtEffectiveDpi = 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
