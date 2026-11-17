using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Serilog;

namespace StickyTodo.App.Services;

/// <summary>
/// 把录好的 GIF 放进剪贴板，同时落一份临时文件。
///
/// 一次 <see cref="Clipboard.SetDataObject"/> 里放三种格式，粘到哪儿都有东西：
///   * <see cref="DataFormats.Bitmap"/>：首帧位图 —— 画图 / 微信 / Word 这类只认图片的程序；
///   * <see cref="GifFormatName"/>（自定义 "GIF"）：原始字节 —— Slack / Discord / 浏览器读它；
///   * <see cref="DataFormats.FileDrop"/>：%TEMP% 里的 .gif 路径 —— 资源管理器、邮件客户端能直接贴文件。
/// </summary>
public static class GifClipboard
{
    /// <summary>自定义剪贴板格式名（放 GIF 原始字节）。</summary>
    public const string GifFormatName = "GIF";

    private const int MaxAttempts = 4;

    /// <summary>
    /// 发布到剪贴板。返回临时文件路径（写失败时为 null）。
    /// 剪贴板本身在独立的 STA 线程里写（大对象要几百毫秒，而且可能被别的程序短暂占用）。
    /// </summary>
    public static string? Publish(byte[] gif)
    {
        if (gif is null || gif.Length == 0)
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"{DateTime.Now:yyyyMMdd-HHmmss}.gif");

        try
        {
            File.WriteAllBytes(path, gif);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[gif] 临时文件写入失败：{Path}", path);
            path = null;
        }

        var preview = CreatePreview(gif);
        var capturedPath = path;

        var thread = new Thread(() => SetClipboard(gif, capturedPath, preview))
        {
            IsBackground = true,
            Name = "gif-clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return path;
    }

    /// <summary>从 GIF 字节里解出首帧，当 <see cref="DataFormats.Bitmap"/> 用。</summary>
    private static BitmapSource? CreatePreview(byte[] gif)
    {
        try
        {
            using var stream = new MemoryStream(gif, writable: false);
            var frame = BitmapFrame.Create(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[gif] 首帧解码失败，剪贴板里不放位图");
            return null;
        }
    }

    private static void SetClipboard(byte[] gif, string? path, BitmapSource? preview)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetData(GifFormatName, new MemoryStream(gif, writable: false));

                if (preview is not null)
                {
                    data.SetData(DataFormats.Bitmap, preview);
                }

                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    data.SetData(DataFormats.FileDrop, new[] { path });
                }

                // copy: true —— 立刻把数据交给系统，本进程退出后剪贴板依然有效
                Clipboard.SetDataObject(data, copy: true);

                Log.Information(
                    "[gif] 已放入剪贴板：GIF({Kb} KB) + Bitmap + FileDrop({Path})",
                    gif.Length / 1024, path ?? "无");
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= MaxAttempts)
                {
                    Log.Warning(ex, "[gif] 剪贴板写入失败（重试 {Attempts} 次）", MaxAttempts);
                    return;
                }

                Thread.Sleep(120);
            }
        }
    }
}
