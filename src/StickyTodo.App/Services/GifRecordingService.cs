using System.Drawing;
using System.Windows;
using Serilog;
using StickyTodo.App.Views;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Services;

/// <summary>
/// GIF 录屏的总入口：选区域、开悬浮条、录完放剪贴板、弹提示条。
/// 具体的抓帧与编码在 <see cref="GifRecorder"/>，这里只管「流程和窗口」。
///
/// 三个入口：
///   * 托盘「录 GIF → 全屏 / 区域」；
///   * 截图遮罩工具条上的「录 GIF」（用当前框选）；
///   * 全局热键 F2（开始/停止，沿用上次的范围）/ F4（立即停止）。
/// </summary>
public sealed class GifRecordingService : IDisposable
{
    private readonly AppSettingsService _settings;

    private GifRecorder? _recorder;
    private GifRecordingBar? _bar;
    private GifToast? _toast;

    /// <summary>最近一次录制的区域（物理像素）：提示条要贴到这块屏上。</summary>
    private Rectangle? _recordedRegion;

    public GifRecordingService(AppSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>正在抓帧。</summary>
    public bool IsRecording => _recorder is { IsRecording: true };

    /// <summary>已经在倒计时 / 正在录 / 正在生成。</summary>
    private bool IsBusy => _bar is not null || _recorder is { IsBusy: true };

    /// <summary>F2：正在录（或在倒计时/生成）就停，否则按上次的范围开始。</summary>
    public void ToggleRecording()
    {
        if (IsBusy)
        {
            StopRecording();
            return;
        }

        StartLastMode();
    }

    /// <summary>F4 / 悬浮条「停止」：收尾并生成 GIF（还在倒计时就直接取消）。</summary>
    public void StopRecording()
    {
        if (_recorder is { IsRecording: true })
        {
            _recorder.Stop();
            return;
        }

        // 倒计时阶段按 F4：这次录制还没开始，直接取消
        _bar?.CancelAndClose();
    }

    /// <summary>整屏（整个虚拟桌面）录制。</summary>
    public void StartFullScreen() => Begin(null);

    /// <summary>用一块屏幕区域（物理像素）录制，区域来自截图遮罩的框选。</summary>
    public void StartRegion(Rectangle regionPx) => Begin(regionPx);

    /// <summary>按上次的方式开始：上次是区域且区域还有效就用它，否则整屏。</summary>
    public void StartLastMode() => Begin(ResolveLastRegion());

    private void Begin(Rectangle? desiredRegion)
    {
        if (IsBusy)
        {
            Log.Information("[gif] 已经在倒计时/录制/生成中，忽略这次的开始请求");
            return;
        }

        // 截图遮罩还开着（F1/F2 都在全局热键里，随时可能按下）：这时候开录，
        // 录进去的会是压暗后的遮罩，先让用户把手上的框选处理完
        if (IsOverlayOpen())
        {
            Log.Information("[gif] 截图遮罩还开着，先不开始录制");
            return;
        }

        var screen = ScreenCaptureService.GetVirtualScreenBounds();
        var region = ClampRegion(desiredRegion ?? screen, screen);

        if (region.Width < 4 || region.Height < 4)
        {
            Log.Warning("[gif] 录制区域无效（{Width}x{Height}），回退整屏", region.Width, region.Height);
            region = ClampRegion(screen, screen);
        }

        var quality = _settings.GifQuality;
        var countdown = _settings.GifCountdown;

        RememberMode(region, screen);
        _recordedRegion = region;
        CloseBar();

        var recorder = new GifRecorder { Quality = quality };
        recorder.Finished += OnFinished;
        recorder.Failed += OnFailed;
        _recorder = recorder;

        var bar = new GifRecordingBar(
            recorder,
            region,
            countdown,
            quality,
            onQualityChanged: qualityKey => _settings.GifQuality = qualityKey,
            onCountdownChanged: enabled => _settings.GifCountdown = enabled);

        bar.Closed += (_, _) =>
        {
            if (ReferenceEquals(_bar, bar))
            {
                _bar = null;
            }

            // 用户直接把悬浮条叉掉/Alt+F4：别留下一个没有 UI 的后台录制
            if (recorder.IsRecording)
            {
                recorder.Cancel();
            }
        };

        _bar = bar;
        bar.Show();
    }

    // ---------------- 完成 / 失败 ----------------

    private void OnFinished(byte[] gif)
    {
        // 从编码线程回来，切回 UI 线程再动窗口
        Dispatch(() =>
        {
            CloseBar();

            if (gif.Length == 0)
            {
                return;
            }

            try
            {
                var path = GifClipboard.Publish(gif);
                ShowToast(gif, path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[gif] 放入剪贴板失败");
                ConfirmWindow.Notify(null, "GIF", "GIF 已生成，但放进剪贴板失败：" + ex.Message);
            }
        });
    }

    private void OnFailed(string message) => Dispatch(() =>
    {
        CloseBar();
        ConfirmWindow.Notify(null, "GIF", message);
    });

    private void ShowToast(byte[] gif, string? path)
    {
        _toast?.Close();

        // 提示条贴到刚录的那块屏的右下角（区域贴边时会自己夹进工作区）
        var toast = new GifToast(gif, path, _recordedRegion);
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_toast, toast))
            {
                _toast = null;
            }
        };

        _toast = toast;
        toast.Show();
    }

    private static void Dispatch(Action action)
    {
        var app = Application.Current;
        if (app is null)
        {
            action();
            return;
        }

        app.Dispatcher.BeginInvoke(action);
    }

    /// <summary>截图遮罩是不是还开着（全局热键不受模态窗口限制，得自己看一眼）。</summary>
    private static bool IsOverlayOpen()
    {
        var app = Application.Current;
        if (app is null)
        {
            return false;
        }

        foreach (Window window in app.Windows)
        {
            if (window is SnipOverlayWindow { IsLoaded: true })
            {
                return true;
            }
        }

        return false;
    }

    // ---------------- 设置记忆 ----------------

    private void RememberMode(Rectangle region, Rectangle screen)
    {
        var fullScreen = region.Left == screen.Left && region.Top == screen.Top
            && region.Width == screen.Width && region.Height == screen.Height;

        _settings.GifLastMode = fullScreen ? "fullscreen" : "region";

        if (!fullScreen)
        {
            _settings.GifLastRegion = $"{region.X},{region.Y},{region.Width},{region.Height}";
        }
    }

    private Rectangle? ResolveLastRegion()
    {
        if (!string.Equals(_settings.GifLastMode, "region", StringComparison.Ordinal))
        {
            return null;
        }

        var raw = _settings.GifLastRegion;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split(',');
        if (parts.Length != 4
            || !int.TryParse(parts[0].Trim(), out var x)
            || !int.TryParse(parts[1].Trim(), out var y)
            || !int.TryParse(parts[2].Trim(), out var w)
            || !int.TryParse(parts[3].Trim(), out var h))
        {
            Log.Debug("[gif] 上次的区域记不下来：{Raw}", raw);
            return null;
        }

        return new Rectangle(x, y, w, h);
    }

    /// <summary>把区域夹进当前虚拟屏（上次的设置在换显示器/改分辨率后可能已经越界）。</summary>
    private static Rectangle ClampRegion(Rectangle region, Rectangle screen)
    {
        var left = Math.Max(region.Left, screen.Left);
        var top = Math.Max(region.Top, screen.Top);
        var right = Math.Min(region.Right, screen.Right);
        var bottom = Math.Min(region.Bottom, screen.Bottom);

        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // ---------------- 收尾 ----------------

    private void CloseBar()
    {
        var bar = _bar;
        _bar = null;

        if (bar is null)
        {
            return;
        }

        try
        {
            bar.Close();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[gif] 关闭悬浮条失败");
        }
    }

    public void Dispose()
    {
        CloseBar();

        try
        {
            _toast?.Close();
        }
        catch (Exception)
        {
            // 退出路径上不抛
        }

        _toast = null;

        var recorder = _recorder;
        _recorder = null;
        recorder?.Dispose();
    }
}
