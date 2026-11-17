using System.Drawing;
using System.Windows;
using Serilog;
using StickyTodo.App.Views;
using StickyTodo.Core.Services;

namespace StickyTodo.App.Services;

/// <summary>
/// GIF 录屏的总入口：先弹录制设置（区域/全屏、尺寸、3 秒倒计时），
/// 再开录制红框 + 悬浮条，录完放剪贴板、弹提示条。
/// 具体的抓帧与编码在 <see cref="GifRecorder"/>，这里只管「流程和窗口」。
///
/// 入口：
///   * 全局热键 F2：开始 / 停止（开始时先出设置卡片）；
///   * 托盘「录 GIF → 区域 / 全屏」（同样先出设置卡片，只是预选不同范围）；
///   * F4 或悬浮条「停止」：立即收尾。
///
/// 和截图（F1）**完全分开**：这里不再借用截图遮罩，框选用的是
/// <see cref="GifRegionPickerWindow"/>（只拖框，没有画笔那套工具）。
/// </summary>
public sealed class GifRecordingService : IDisposable
{
    private readonly AppSettingsService _settings;

    private GifRecorder? _recorder;
    private GifRecordingBar? _bar;
    private GifFrameWindow? _frame;
    private GifToast? _toast;

    /// <summary>最近一次录制的区域（物理像素）：提示条要贴到这块屏上。</summary>
    private Rectangle? _recordedRegion;

    public GifRecordingService(AppSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>正在抓帧。</summary>
    public bool IsRecording => _recorder is { IsRecording: true };

    /// <summary>上次录过的区域（物理像素）；没录过就退回主屏，给托盘「区域」做预填。</summary>
    public Rectangle LastRegion =>
        ResolveLastRegion() ?? ScreenCaptureService.GetScreens().FirstOrDefault().Bounds;

    /// <summary>已经在倒计时 / 正在录 / 正在生成。</summary>
    private bool IsBusy => _bar is not null || _recorder is { IsBusy: true };

    /// <summary>F2：正在录（或在倒计时/生成）就停，否则先弹录制设置。</summary>
    public void ToggleRecording()
    {
        if (IsBusy)
        {
            StopRecording();
            return;
        }

        StartFromSetup(null, fullScreen: null);
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

    /// <summary>托盘「全屏」：设置卡片预选全屏。</summary>
    public void StartFullScreen() => StartFromSetup(null, fullScreen: true);

    /// <summary>托盘「区域」：设置卡片预选区域，并带上上次的尺寸。</summary>
    public void StartRegion(Rectangle regionPx) => StartFromSetup(regionPx, fullScreen: false);

    /// <summary>
    /// 先出录制设置卡片（区域 / 全屏、区域尺寸、是否等 3 秒、清晰度），
    /// 用户确认后才真正开始；取消就什么都不做。
    /// </summary>
    private void StartFromSetup(Rectangle? region, bool? fullScreen)
    {
        if (IsBusy)
        {
            Log.Information("[gif] 已经在倒计时/录制/生成中，忽略这次的开始请求");
            return;
        }

        var setup = new GifSetupWindow(
            region ?? ResolveLastRegion() ?? ScreenCaptureService.GetScreens().FirstOrDefault().Bounds,
            fullScreen ?? _settings.GifLastMode == AppSettingsService.GifModeFullScreen,
            _settings.GifCountdown,
            _settings.GifQuality);

        if (setup.ShowDialog() != true)
        {
            Log.Information("[gif] 设置卡片里取消了录制");
            return;
        }

        _settings.GifCountdown = setup.WaitThreeSeconds;
        _settings.GifQuality = setup.Quality;

        if (setup.ResultFullScreen)
        {
            BeginFullScreen();
            return;
        }

        if (setup.ResultRegion is { } chosen)
        {
            Begin(chosen);
        }
    }

    /// <summary>多屏时先让用户选一块屏，然后按那块屏的边界录。</summary>
    private void BeginFullScreen()
    {
        if (IsBusy)
        {
            return;
        }

        var screens = ScreenCaptureService.GetScreens();

        if (screens.Count > 1)
        {
            var picker = new GifScreenPickerWindow(screens);
            var picked = picker.ShowDialog() == true ? picker.Selected : null;

            if (picked is null)
            {
                Log.Information("[gif] 多屏下没有选屏（{Count} 块屏），放弃这次全屏录制", screens.Count);
                return;
            }

            Log.Information("[gif] 选了 {Index} 号屏 {Width}x{Height} @ {X},{Y}",
                picked.Value.Index, picked.Value.Bounds.Width, picked.Value.Bounds.Height,
                picked.Value.Bounds.X, picked.Value.Bounds.Y);

            Begin(picked.Value.Bounds);
            return;
        }

        Begin(null);
    }

    private void Begin(Rectangle? desiredRegion)
    {
        if (IsBusy)
        {
            Log.Information("[gif] 已经在倒计时/录制/生成中，忽略这次的开始请求");
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

        // 到这里区域已经是物理像素（框选换算见 GifRegionPickerWindow.ToPixelRect，
        // 全屏/选屏用的是 Screen.Bounds），再记一条 DPI 方便用户核对录出来的分辨率
        Log.Information(
            "[gif] 录制区域 {Width}x{Height} @ {X},{Y} 物理像素 · 屏缩放 {Scale:P0}",
            region.Width, region.Height, region.X, region.Y, ScreenCaptureService.DpiScaleFor(region));

        RememberMode(region);
        _recordedRegion = region;
        CloseBar();
        ShowFrame(region);

        var recorder = new GifRecorder { Quality = quality };
        recorder.Finished += bytes => OnFinished(recorder, bytes);
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

            CloseFrame();
        };

        _bar = bar;
        bar.Show();
    }

    // ---------------- 录制红框 ----------------

    private void ShowFrame(Rectangle region)
    {
        CloseFrame();

        try
        {
            var frame = new GifFrameWindow(region);
            frame.Show();
            _frame = frame;
        }
        catch (Exception ex)
        {
            // 红框只是提示，画不出来不影响录制
            Log.Debug(ex, "[gif] 录制红框打开失败");
        }
    }

    private void CloseFrame()
    {
        var frame = _frame;
        _frame = null;

        if (frame is null)
        {
            return;
        }

        try
        {
            frame.Close();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[gif] 关闭录制红框失败");
        }
    }

    // ---------------- 完成 / 失败 ----------------

    private void OnFinished(GifRecorder recorder, byte[] gif)
    {
        // 从编码线程回来，切回 UI 线程再动窗口
        Dispatch(() =>
        {
            CloseBar();
            CloseFrame();

            if (gif.Length == 0)
            {
                return;
            }

            try
            {
                var path = GifClipboard.Publish(gif);
                ShowToast(gif, path, recorder.LastSize);
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
        CloseFrame();
        ConfirmWindow.Notify(null, "GIF", message);
    });

    private void ShowToast(byte[] gif, string? path, System.Drawing.Size? gifSize)
    {
        _toast?.Close();

        // 提示条贴到刚录的那块屏的右下角（区域贴边时会自己夹进工作区）
        var toast = new GifToast(gif, path, _recordedRegion, gifSize);
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

    // ---------------- 设置记忆 ----------------

    /// <summary>
    /// 记住这次的录制方式。区域正好等于整块虚拟屏（或某一块屏 —— 多屏选屏录的就是单屏）
    /// 就算「全屏」，下次 F2 还是全屏，不会把一次全屏录成一个巨大区域。
    /// </summary>
    private void RememberMode(Rectangle region)
    {
        var fullScreen = region == ScreenCaptureService.GetVirtualScreenBounds()
            || ScreenCaptureService.GetScreens().Any(s => s.Bounds == region);

        _settings.GifLastMode = fullScreen ? AppSettingsService.GifModeFullScreen : AppSettingsService.GifModeRegion;

        if (!fullScreen)
        {
            _settings.GifLastRegion = $"{region.X},{region.Y},{region.Width},{region.Height}";
        }
    }

    private Rectangle? ResolveLastRegion()
    {
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
        CloseFrame();

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
