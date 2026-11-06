using System.Windows;
using System.Windows.Media.Imaging;
using Serilog;
using StickyTodo.App.Views;

namespace StickyTodo.App.Services;

/// <summary>
/// 贴图管理：F1 截图 → 编辑 → 贴到桌面；F3 把上一次的截图再贴一张。
///
/// 贴图是独立的置顶小窗（不是看板），可以同时存在很多张，
/// 关掉最后一张不影响程序，也不会进「会话恢复」。
/// </summary>
public sealed class PinManager
{
    private readonly List<PinWindow> _pins = new();

    private BitmapSource? _lastImage;
    private Point? _lastTopLeft;

    /// <summary>当前贴着的图片数量。</summary>
    public int Count => _pins.Count;

    /// <summary>有没有「上一次的截图」可以直接再贴一张（F3 用）。</summary>
    public bool HasLastImage => _lastImage is not null;

    /// <summary>
    /// F1：截取整个屏幕 → 让用户框选/标注 → 贴到桌面。
    /// 返回贴出来的窗口；用户取消则返回 null。
    /// </summary>
    public PinWindow? CaptureAndPin()
    {
        var screenshot = ScreenCaptureService.CaptureVirtualScreen();
        if (screenshot is null)
        {
            Log.Warning("[pin] 抓屏失败");
            return null;
        }

        var overlay = new SnipOverlayWindow(screenshot)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        var accepted = overlay.ShowDialog() == true;
        var result = overlay.Result;

        if (!accepted || result is null)
        {
            return null;
        }

        _lastImage = result;
        _lastTopLeft = overlay.SelectionTopLeft;

        // 「复制」按钮：图已经进剪贴板了，不用再贴一张
        if (overlay.CopiedOnly)
        {
            Log.Information("[pin] 已复制到剪贴板：{Width}x{Height}（未贴到桌面）",
                result.PixelWidth, result.PixelHeight);
            return null;
        }

        Log.Information("[pin] 截图已贴出：{Width}x{Height}（同时已复制到剪贴板）",
            result.PixelWidth, result.PixelHeight);
        return Pin(result, _lastTopLeft);
    }

    /// <summary>F3：把上一次的截图再贴一张（每次错开一点，避免完全重叠）。</summary>
    public PinWindow? PinLast()
    {
        if (_lastImage is null)
        {
            // 没有截图记录就试试剪贴板里的图片
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } clipboardImage)
            {
                _lastImage = clipboardImage;
                _lastTopLeft = null;
            }
            else
            {
                Log.Information("[pin] 没有可贴的截图");
                return null;
            }
        }

        var offset = (_pins.Count % 6) * 24;
        var origin = _lastTopLeft;

        Point? position = origin is null
            ? null
            : new Point(origin.Value.X + offset, origin.Value.Y + offset);

        Log.Information("[pin] 再贴一张（当前 {Count} 张）", _pins.Count + 1);
        return Pin(_lastImage, position);
    }

    /// <summary>贴出一张图。</summary>
    public PinWindow Pin(BitmapSource image, Point? topLeft)
    {
        var window = new PinWindow(image, topLeft);
        window.PinClosed += (_, _) => _pins.Remove(window);

        _pins.Add(window);
        window.Show();

        return window;
    }

    /// <summary>解锁所有贴图（锁定时窗口是鼠标穿透的，托盘菜单留个总开关）。</summary>
    public int UnlockAll()
    {
        var count = 0;

        foreach (var pin in _pins.ToList())
        {
            if (pin.IsLockedPin)
            {
                pin.SetLocked(false);
                count++;
            }
        }

        return count;
    }

    /// <summary>关掉所有贴图。</summary>
    public int CloseAll()
    {
        var count = _pins.Count;

        foreach (var pin in _pins.ToList())
        {
            pin.Close();
        }

        _pins.Clear();
        return count;
    }
}
