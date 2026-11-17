using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Serilog;
using StickyTodo.App.Services;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Views;

/// <summary>
/// 录制中悬浮条：红点（呼吸）+ 计时 + 帧数 + 清晰度 + 倒计时开关 + 停止 / 取消。
///
/// 摆位原则：**别把工具条本身录进去**。优先放在录制区域的上下左右外侧；
/// 整屏录制时四周没有空位，只能退到离区域中心最远的那个角（这时会被录进去一点，
/// 这是所有「录屏不录自己」都绕不开的取舍，所以默认是全屏时也尽量缩在最角落）。
/// </summary>
public partial class GifRecordingBar : Window
{
    private readonly GifRecorder _recorder;
    private readonly Rectangle _region;
    private readonly bool _countdown;
    private readonly Action<string>? _onQualityChanged;
    private readonly Action<bool>? _onCountdownChanged;

    private DispatcherTimer? _countdownTimer;
    private int _countdownLeft;

    private bool _stopping;

    public GifRecordingBar(
        GifRecorder recorder,
        Rectangle region,
        bool countdown,
        string quality,
        Action<string>? onQualityChanged = null,
        Action<bool>? onCountdownChanged = null)
    {
        InitializeComponent();

        _recorder = recorder;
        _region = region;
        _countdown = countdown;
        _onQualityChanged = onQualityChanged;
        _onCountdownChanged = onCountdownChanged;

        StatusText.Text = Loc.T("gif.recording");
        StopButton.Content = Loc.T("gif.stop");
        QualityLabel.Text = Loc.T("gif.quality");
        CountdownBox.Content = Loc.T("gif.countdown");
        CloseButton.ToolTip = Loc.T("common.cancel");

        CountdownBox.IsChecked = countdown;
        CountdownBox.Click += (_, _) => _onCountdownChanged?.Invoke(CountdownBox.IsChecked == true);

        QualityBox.Items.Add(new ComboBoxItem { Content = Loc.T("gif.qualityLow"), Tag = GifQualityPresets.Low });
        QualityBox.Items.Add(new ComboBoxItem { Content = Loc.T("gif.qualityMedium"), Tag = GifQualityPresets.Medium });
        QualityBox.Items.Add(new ComboBoxItem { Content = Loc.T("gif.qualityHigh"), Tag = GifQualityPresets.High });
        QualityBox.SelectedIndex = GifQualityPresets.Normalize(quality) switch
        {
            GifQualityPresets.Low => 0,
            GifQualityPresets.High => 2,
            _ => 1,
        };
        QualityBox.SelectionChanged += Quality_Changed;

        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceAwayFromRegion();
        TryExcludeFromCapture();

        _recorder.FrameTick += OnFrameTick;
        _recorder.EncodingStarted += OnEncodingStarted;

        if (_countdown)
        {
            BeginCountdown();
        }
        else
        {
            BeginRecording();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _recorder.FrameTick -= OnFrameTick;
        _recorder.EncodingStarted -= OnEncodingStarted;
        StopCountdownTimer();
        StopBreathing();
    }

    // ---------------- 倒计时 → 开始 ----------------

    private void BeginCountdown()
    {
        _countdownLeft = 3;
        ShowCountdownNumber();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            _countdownLeft--;

            if (_countdownLeft <= 0)
            {
                StopCountdownTimer();
                BeginRecording();
                return;
            }

            ShowCountdownNumber();
        };
        _countdownTimer.Start();
    }

    private void ShowCountdownNumber()
    {
        CountdownText.Text = _countdownLeft.ToString();
        CountdownText.Visibility = Visibility.Visible;
    }

    private void StopCountdownTimer()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
    }

    private void BeginRecording()
    {
        CountdownText.Visibility = Visibility.Hidden;
        StatusText.Text = Loc.T("gif.recording");
        StartBreathing();

        if (!_recorder.Start(_region, _recorder.Quality))
        {
            Log.Warning("[gif] 悬浮条启动录制失败（可能已经在录）");
            Close();
        }
    }

    // ---------------- 录制 / 编码状态 ----------------

    private void OnFrameTick(TimeSpan elapsed) => Dispatcher.BeginInvoke(new Action(() =>
    {
        TimeText.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        FrameText.Text = $"#{_recorder.FrameCount}";
    }));

    private void OnEncodingStarted() => Dispatcher.BeginInvoke(new Action(() =>
    {
        StopBreathing();
        Dot.Fill = System.Windows.Media.Brushes.Gray;
        StopButton.IsEnabled = false;
        // 这里没有对应的文案 key（gif.* 里只覆盖了录制/停止），保持中文与截图工具条一致
        StatusText.Text = "正在生成…";
        FrameText.Text = $"#{_recorder.FrameCount}";
    }));

    private void StartBreathing()
    {
        if (!Motion.Enabled)
        {
            Dot.Opacity = 1;
            return;
        }

        var animation = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        Dot.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopBreathing() => Dot.BeginAnimation(OpacityProperty, null);

    // ---------------- 交互 ----------------

    private void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (QualityBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        // 帧率/分辨率/色数都在开始抓帧那一刻定了，录制中改只影响「下一次」，
        // 这里同步给 recorder 只是为了把选择留在设置里。
        _recorder.Quality = tag;
        _onQualityChanged?.Invoke(tag);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        StopButton.IsEnabled = false;
        StopCountdownTimer();
        StopBreathing();

        if (_recorder.IsRecording)
        {
            // 收尾：抓帧线程退出后会自己进入编码 → Finished
            _recorder.Stop();
            return;
        }

        // 还在倒计时（没真正开始抓帧）：当作取消
        _recorder.Cancel();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CancelAndClose();

    /// <summary>取消这次录制并关掉悬浮条（「关闭」「Esc」「倒计时中按 F4」都走这里）。</summary>
    public void CancelAndClose()
    {
        _stopping = true;
        StopCountdownTimer();
        StopBreathing();
        _recorder.Cancel();

        if (IsLoaded)
        {
            Close();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        CancelAndClose();
        e.Handled = true;
    }

    // ---------------- 摆位 ----------------

    /// <summary>
    /// 优先放到录制区域外面（上 / 下 / 左 / 右），整屏录制时退到离区域中心最远的角。
    /// 用像素算、再折成 DIP 赋予 Left/Top：和高 DPI 下 SetWindowPos 的效果一致，
    /// 而且之后窗口因为文字变化改尺寸时不会跑回屏幕中间。
    /// </summary>
    private void PlaceAwayFromRegion()
    {
        UpdateLayout();

        var handle = new WindowInteropHelper(this).Handle;
        var scale = handle != IntPtr.Zero ? GetDpiForWindow(handle) / 96.0 : 1.0;
        if (scale <= 0)
        {
            scale = 1;
        }

        var width = Math.Max(120, (int)Math.Ceiling((ActualWidth > 0 ? ActualWidth : Width) * scale));
        var height = Math.Max(28, (int)Math.Ceiling((ActualHeight > 0 ? ActualHeight : 40) * scale));

        var screen = ScreenCaptureService.GetVirtualScreenBounds();
        const int gap = 8;

        var outside = new[]
        {
            new Rectangle(_region.Left, _region.Top - height - gap, width, height),
            new Rectangle(_region.Left, _region.Bottom + gap, width, height),
            new Rectangle(_region.Left - width - gap, _region.Top, width, height),
            new Rectangle(_region.Right + gap, _region.Top, width, height),
        };

        foreach (var candidate in outside)
        {
            if (screen.Contains(candidate) && !candidate.IntersectsWith(_region))
            {
                Apply(candidate);
                return;
            }
        }

        var corners = new[]
        {
            new Rectangle(screen.Left + gap, screen.Top + gap, width, height),
            new Rectangle(screen.Right - width - gap, screen.Top + gap, width, height),
            new Rectangle(screen.Left + gap, screen.Bottom - height - gap, width, height),
            new Rectangle(screen.Right - width - gap, screen.Bottom - height - gap, width, height),
        };

        var centerX = _region.Left + _region.Width / 2.0;
        var centerY = _region.Top + _region.Height / 2.0;

        var best = corners[0];
        var bestDistance = -1.0;

        foreach (var corner in corners)
        {
            var dx = corner.Left + corner.Width / 2.0 - centerX;
            var dy = corner.Top + corner.Height / 2.0 - centerY;
            var distance = dx * dx + dy * dy;

            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = corner;
            }
        }

        Apply(best);

        void Apply(Rectangle rect)
        {
            // 屏幕物理像素 → DIP（WPF 的 Left/Top 是 DIP）
            Left = rect.Left / scale;
            Top = rect.Top / scale;
            Log.Debug("[gif] 悬浮条摆到 {X},{Y}（区域 {RX},{RY} {RW}x{RH}）",
                rect.Left, rect.Top, _region.Left, _region.Top, _region.Width, _region.Height);
        }
    }

    /// <summary>
    /// 再补一层保险：让 Windows 把这条悬浮条从屏幕捕获里排除掉
    /// （WDA_EXCLUDEFROMCAPTURE，Win10 2004+）。成功的话整屏录制也不会录到工具条，
    /// 失败（老系统 / 分层窗口不支持）就只靠上面的几何摆位躲开。
    /// </summary>
    private void TryExcludeFromCapture()
    {
        const uint WdaExcludeFromCapture = 0x11;

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (SetWindowDisplayAffinity(handle, WdaExcludeFromCapture))
            {
                Log.Debug("[gif] 悬浮条已从屏幕捕获中排除");
            }
            else
            {
                Log.Debug("[gif] 悬浮条无法从捕获中排除（win32={Error}），改为躲到录制区域外",
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[gif] 设置显示亲和性失败");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
