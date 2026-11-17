using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Serilog;
using StickyTodo.Core.Media;

namespace StickyTodo.App.Services;

/// <summary>
/// 一档清晰度：帧率 / 单帧最长边上限 / 色数。三者一起决定 GIF 的体积与观感。
/// <see cref="MaxDimension"/> 为 0 表示**保持原始分辨率**，不做任何缩放（也不放大）。
/// </summary>
public readonly record struct GifQualityPreset(int Fps, int MaxDimension, int Colors)
{
    /// <summary>这一档的抓帧间隔（毫秒）。</summary>
    public int IntervalMs => Math.Max(16, 1000 / Math.Max(1, Fps));

    /// <summary>这一档要不要缩小（原始尺寸本来就更小时不缩，绝不放大）。</summary>
    public bool ShouldDownscale(int width, int height) =>
        MaxDimension > 0 && Math.Max(width, height) > MaxDimension;
}

/// <summary>清晰度档位名（与设置里的取值一一对应）。</summary>
public static class GifQualityPresets
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    public static string Normalize(string? quality) => quality?.Trim().ToLowerInvariant() switch
    {
        Low => Low,
        High => High,
        _ => Medium,
    };

    /// <summary>保持原始分辨率（不缩放）。</summary>
    public const int OriginalSize = 0;

    /// <summary>
    /// 档位只在**需要压体积**时才缩小（原始尺寸本来更小就原样录），一律不放大。
    /// 录屏区域 1920×1080、选「高清」时 GIF 就是 1920×1080。
    /// </summary>
    public static GifQualityPreset Resolve(string? quality) => Normalize(quality) switch
    {
        // 小文件：8fps / 长边 960 / 64 色 —— 发群里、贴 issue 够用
        Low => new GifQualityPreset(8, 960, 64),

        // 高清：20fps / 原始分辨率 / 256 色 —— 演示动效、鼠标轨迹看得清
        High => new GifQualityPreset(20, OriginalSize, 256),

        // 标准：12fps / 长边 1280 / 128 色
        _ => new GifQualityPreset(12, 1280, 128),
    };
}

/// <summary>
/// GIF 录屏引擎（只管抓帧和编码，不碰任何窗口）。
///
/// 抓帧用 GDI 的 CopyFromScreen **直接抓录制区域**：这台机器整屏一次 BitBlt 约 108ms，
/// 先抓整屏再裁的话 20fps 根本跑不动。抓到的帧按清晰度档位缩放（默认不缩）并转成 BGRA 存内存。
///
/// 分辨率就是录制区域的**物理像素**：1920×1080 的区域，抓到的位图也是 1920×1080。
/// 只有「中 / 小文件」两档在原始尺寸更大时才缩到长边 1280 / 960，永远不放大。
///
/// 上限：最长 30 秒 / 300 帧 / 帧数据 320MB，最先到的那条自动收尾
/// （GIF 越大越没用，聊天软件基本都在 8-10MB 开始不耐烦）。帧数据到顶时**先抽帧**
/// （分辨率不动，帧率减半、时长不变），不够再抽，不会因为内存把一段录制早早掐断。
///
/// 线程模型：抓帧和编码都在后台线程，完成时通过事件回到调用方；
/// <see cref="FrameTick"/> 是从抓帧线程抛出来的，UI 侧自己 Dispatcher 切回去。
/// </summary>
public sealed class GifRecorder : IDisposable
{
    /// <summary>最多录多少帧。</summary>
    public const int MaxFrames = 300;

    /// <summary>最长录制时长。</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 帧数据的内存上限。每帧是整幅 BGRA：1920×1080 一帧约 8.3MB，
    /// 300 帧就是 2.4GB —— 所以设这道闸，到顶就把已录的帧抽掉一半继续录
    /// （分辨率不降、播放时长不变，只是帧率低一点）。
    /// </summary>
    public const long MaxBufferBytes = 320L * 1024 * 1024;

    /// <summary>GIF 的延时是 1/100 秒，低于 2 会被一些播放器当 0 处理成 10。</summary>
    private const int MinFrameDelayMs = 20;

    private const int MaxFrameDelayMs = 1000;

    private enum RecordState
    {
        Idle,
        Recording,
        Encoding,
    }

    private readonly object _gate = new();

    private readonly List<GifFrame> _frames = new();

    /// <summary>每帧开始抓取的时间点（毫秒），用来算真实帧间隔。</summary>
    private readonly List<int> _timeMarks = new();

    private RecordState _state = RecordState.Idle;
    private Thread? _thread;
    private ManualResetEvent? _stop;
    private Rectangle _region;
    private GifQualityPreset _preset;
    private bool _discard;

    /// <summary>抓帧进度（已录时长），从抓帧线程触发。</summary>
    public event Action<TimeSpan>? FrameTick;

    /// <summary>抓帧结束、开始建调色板与编码（UI 可以显示「正在生成…」）。</summary>
    public event Action? EncodingStarted;

    /// <summary>编码完成，参数是 GIF 字节。</summary>
    public event Action<byte[]>? Finished;

    /// <summary>出错（区域非法 / 一帧都没抓到 / 编码异常）。</summary>
    public event Action<string>? Failed;

    /// <summary>清晰度档位（low / medium / high）。下一次 <see cref="Start"/> 生效。</summary>
    public string Quality { get; set; } = GifQualityPresets.Medium;

    /// <summary>
    /// 最近一次生成的 GIF 尺寸（像素）。<see cref="Finished"/> 触发时已经写好，
    /// 提示条拿它显示「1920×1080」这样的实际分辨率，用户可以核对录制区域对不对。
    /// </summary>
    public Size? LastSize { get; private set; }

    /// <summary>
    /// 是否把鼠标指针画进帧里（默认开）。看演示动效时没有指针就看不出「点了哪里」。
    /// 下一次 <see cref="Start"/> 生效。
    /// </summary>
    public bool CaptureCursor { get; set; } = true;

    /// <summary>正在抓帧。</summary>
    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _state == RecordState.Recording;
            }
        }
    }

    /// <summary>在抓帧或在编码（这段时间不要开第二次录制）。</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _state != RecordState.Idle;
            }
        }
    }

    /// <summary>已经录到的帧数。</summary>
    public int FrameCount
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>
    /// 开始录制一块屏幕区域。区域必须是**物理像素**（不是 DIP）：
    /// 125% / 150% 缩放下把 DIP 矩形直接喂给 CopyFromScreen，录出来会小一圈还偏。
    /// 返回 false 表示没能开始。
    /// </summary>
    public bool Start(Rectangle regionPx, string? quality = null)
    {
        lock (_gate)
        {
            if (_state != RecordState.Idle)
            {
                return false;
            }

            if (regionPx.Width < 2 || regionPx.Height < 2)
            {
                Serilog.Log.Warning("[gif] 录制区域太小：{Width}x{Height}", regionPx.Width, regionPx.Height);
                return false;
            }

            // GIF 的宽高是整数：区域先对齐到偶数，缩放时边缘不会录进 1px 的抖动/黑边
            var width = Math.Max(2, regionPx.Width & ~1);
            var height = Math.Max(2, regionPx.Height & ~1);
            _region = new Rectangle(regionPx.X, regionPx.Y, width, height);
            _preset = GifQualityPresets.Resolve(quality ?? Quality);
            Quality = GifQualityPresets.Normalize(quality ?? Quality);
            _frames.Clear();
            _timeMarks.Clear();
            LastSize = null;
            _discard = false;
            _stop = new ManualResetEvent(false);
            _state = RecordState.Recording;

            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "gif-capture",
            };
            _thread.Start();
        }

        Log.Information(
            "[gif] 开始录制 {Width}x{Height} @ {X},{Y}（物理像素）· 清晰度 {Quality}（{Fps}fps / 最长边 {Max} / {Colors}色）",
            _region.Width, _region.Height, _region.X, _region.Y, Quality,
            _preset.Fps,
            _preset.MaxDimension > 0 ? $"{_preset.MaxDimension}px" : "原始分辨率",
            _preset.Colors);

        return true;
    }

    /// <summary>停止抓帧，随后自动进入编码 → <see cref="Finished"/>。</summary>
    public void Stop() => SignalStop(discard: false);

    /// <summary>放弃这次录制（不编码、不出图）。</summary>
    public void Cancel() => SignalStop(discard: true);

    private void SignalStop(bool discard)
    {
        lock (_gate)
        {
            if (_state != RecordState.Recording)
            {
                return;
            }

            if (discard)
            {
                _discard = true;
            }

            _stop?.Set();
        }
    }

    // ---------------- 抓帧 ----------------

    private void CaptureLoop()
    {
        Rectangle region;
        GifQualityPreset preset;
        ManualResetEvent stop;
        bool drawCursor;

        lock (_gate)
        {
            region = _region;
            preset = _preset;
            stop = _stop!;
            drawCursor = CaptureCursor;
        }

        // 尺寸在这里一次算定：整段录制所有帧都用这一对宽高（GIF 要求帧尺寸一致）。
        // 只有原始尺寸超过档位上限时才缩，且永远不放大；结果对齐到偶数。
        var downscale = preset.ShouldDownscale(region.Width, region.Height);
        var scale = downscale
            ? preset.MaxDimension / (double)Math.Max(region.Width, region.Height)
            : 1d;
        var targetWidth = AlignEven(downscale ? (int)Math.Round(region.Width * scale) : region.Width);
        var targetHeight = AlignEven(downscale ? (int)Math.Round(region.Height * scale) : region.Height);
        var interval = preset.IntervalMs;

        Log.Information(
            "[gif] 抓帧 {Width}x{Height} → GIF {TargetW}x{TargetH}{Scaled}",
            region.Width, region.Height, targetWidth, targetHeight,
            downscale ? "（按档位缩小）" : "（原始分辨率）");

        Bitmap? raw = null;
        Bitmap? scaled = null;

        try
        {
            raw = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);

            if (targetWidth != region.Width || targetHeight != region.Height)
            {
                scaled = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            }

            using var rawGraphics = Graphics.FromImage(raw);
            using var scaledGraphics = scaled is null ? null : Graphics.FromImage(scaled);

            if (scaledGraphics is not null)
            {
                scaledGraphics.InterpolationMode = InterpolationMode.Bilinear;
                scaledGraphics.PixelOffsetMode = PixelOffsetMode.Half;
                scaledGraphics.CompositingMode = CompositingMode.SourceCopy;
            }

            var clock = Stopwatch.StartNew();
            var failedFrames = 0;
            long bufferedBytes = 0;

            // 帧数据吃紧时抽帧的步长：1 = 每帧都留，2 = 留一半（帧间隔由时间戳算，播放时长不变）
            var thin = 1;
            var captured = 0;

            while (!stop.WaitOne(0))
            {
                var frameStart = (int)clock.ElapsedMilliseconds;

                try
                {
                    // 每帧都新建 Graphics 太慢，复用；CopyFromScreen 本身会整块覆盖
                    rawGraphics.CopyFromScreen(
                        region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);

                    // 指针在缩放之前画进原图：它会跟着整幅画面一起缩，比例才对得上
                    if (drawCursor)
                    {
                        GifCursorPainter.Draw(rawGraphics, region);
                    }

                    Bitmap source;
                    if (scaled is not null && scaledGraphics is not null)
                    {
                        scaledGraphics.DrawImage(
                            raw,
                            new Rectangle(0, 0, targetWidth, targetHeight),
                            0, 0, raw.Width, raw.Height,
                            GraphicsUnit.Pixel);
                        source = scaled;
                    }
                    else
                    {
                        source = raw;
                    }

                    var bgra = CopyToBgra(source);

                    // 抽帧只影响帧率，分辨率不动
                    if (thin <= 1 || captured % thin == 0)
                    {
                        PushFrame(new GifFrame(bgra, targetWidth, targetHeight, interval), frameStart);
                        bufferedBytes += bgra.Length;
                    }

                    captured++;
                    failedFrames = 0;
                }
                catch (Exception ex)
                {
                    failedFrames++;

                    // 偶发失败（切用户、锁屏、UAC 弹窗）跳过这一帧；连续失败就别硬撑了
                    if (failedFrames >= 15)
                    {
                        Log.Warning(ex, "[gif] 连续抓帧失败，停止录制");
                        break;
                    }
                }

                FrameTick?.Invoke(clock.Elapsed);

                int count;
                lock (_gate)
                {
                    count = _frames.Count;
                }

                if (count >= MaxFrames)
                {
                    Log.Information("[gif] 达到 {Max} 帧上限，自动停止", MaxFrames);
                    break;
                }

                if (clock.Elapsed >= MaxDuration)
                {
                    Log.Information("[gif] 达到 {Seconds} 秒上限，自动停止", (int)MaxDuration.TotalSeconds);
                    break;
                }

                if (bufferedBytes >= MaxBufferBytes)
                {
                    // 内存到顶不掐录制：把已录的帧抽掉一半，接着按更低的帧率录下去
                    // （原始分辨率不能动 —— 用户要的就是「录多大就多大」）
                    var before = bufferedBytes;
                    bufferedBytes = ThinOutLocked();
                    thin = Math.Min(thin * 2, 8);

                    Log.Information(
                        "[gif] 帧数据 {Before} MB 超过上限，抽帧后 {After} MB 继续录（每 {Thin} 帧留 1 帧）",
                        before / (1024 * 1024), bufferedBytes / (1024 * 1024), thin);
                }
                else if (thin > 1 && bufferedBytes < MaxBufferBytes / 2)
                {
                    // 抽过一半后内存又宽裕了，把帧率慢慢还回去，别整段都按低帧率录
                    thin /= 2;
                }

                var wait = interval - (int)(clock.ElapsedMilliseconds - frameStart);
                if (wait > 0)
                {
                    stop.WaitOne(wait);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[gif] 抓帧线程异常");
            lock (_gate)
            {
                _state = RecordState.Idle;
            }

            Failed?.Invoke("抓屏失败：" + ex.Message);
            return;
        }
        finally
        {
            scaled?.Dispose();
            raw?.Dispose();
        }

        bool discard;
        List<GifFrame> frames;

        lock (_gate)
        {
            discard = _discard;
            frames = BuildTimedFramesLocked();
            _state = discard ? RecordState.Idle : RecordState.Encoding;
        }

        if (discard)
        {
            Log.Information("[gif] 已放弃这次录制（{Frames} 帧）", frames.Count);
            return;
        }

        if (frames.Count == 0)
        {
            lock (_gate)
            {
                _state = RecordState.Idle;
            }

            Log.Warning("[gif] 一帧都没抓到");
            Failed?.Invoke("一帧都没抓到，请重试");
            return;
        }

        EncodingStarted?.Invoke();
        Encode(frames);
    }

    /// <summary>按抓帧时间点算出每帧的真实延时（抓帧慢的时候 GIF 也不会变快）。</summary>
    private List<GifFrame> BuildTimedFramesLocked()
    {
        var frames = new List<GifFrame>(_frames.Count);
        var nominal = Math.Clamp(_preset.IntervalMs, MinFrameDelayMs, MaxFrameDelayMs);

        for (var i = 0; i < _frames.Count; i++)
        {
            var delay = nominal;
            if (i + 1 < _frames.Count && i + 1 < _timeMarks.Count)
            {
                delay = Math.Clamp(_timeMarks[i + 1] - _timeMarks[i], MinFrameDelayMs, MaxFrameDelayMs);
            }

            frames.Add(_frames[i] with { DelayMs = delay });
        }

        return frames;
    }

    private void PushFrame(GifFrame frame, int timeMark)
    {
        lock (_gate)
        {
            // 首帧尺寸就是整段 GIF 的尺寸：后面万一有尺寸不一致的帧，宁可丢掉
            if (_frames.Count > 0
                && (_frames[0].Width != frame.Width || _frames[0].Height != frame.Height))
            {
                return;
            }

            _frames.Add(frame);
            _timeMarks.Add(timeMark);
        }
    }

    /// <summary>
    /// 把已录的帧抽掉一半（保留偶数序号的那半），返回剩下的帧数据字节数。
    /// 帧间隔是按抓帧时间戳算的，所以抽帧后播放时长不变，只是帧率低一点。
    /// </summary>
    private long ThinOutLocked()
    {
        lock (_gate)
        {
            var kept = 0;
            long bytes = 0;

            for (var i = 0; i < _frames.Count; i += 2)
            {
                var frame = _frames[i];
                _frames[kept] = frame;
                _timeMarks[kept] = i < _timeMarks.Count ? _timeMarks[i] : 0;
                bytes += frame.Bgra.Length;
                kept++;
            }

            if (_frames.Count > kept)
            {
                _frames.RemoveRange(kept, _frames.Count - kept);
            }

            if (_timeMarks.Count > kept)
            {
                _timeMarks.RemoveRange(kept, _timeMarks.Count - kept);
            }

            return bytes;
        }
    }

    /// <summary>对齐到偶数（GIF 的宽高是整数，奇偶不一致会在缩放时抖出 1px 边）。</summary>
    private static int AlignEven(int value) => Math.Max(2, value & ~1);

    private static byte[] CopyToBgra(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var buffer = new byte[width * height * 4];
            var rowBytes = width * 4;
            var stride = Math.Abs(data.Stride);
            var row = new byte[stride];

            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, stride);
                Buffer.BlockCopy(row, 0, buffer, y * rowBytes, rowBytes);
            }

            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    // ---------------- 编码 ----------------

    private void Encode(List<GifFrame> frames)
    {
        var preset = _preset;
        var width = frames[0].Width;
        var height = frames[0].Height;
        var count = frames.Count;

        // 都在后台线程做：建调色板 + 量化 + LZW，300 帧要一两秒，不能卡 UI
        Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();

            try
            {
                var samples = GifEncoder.SampleFrames(frames, 12);
                var palette = GifEncoder.BuildPalette(samples, preset.Colors);

                var indexed = new List<(byte[] Indices, int DelayMs)>(count);
                foreach (var frame in frames)
                {
                    indexed.Add((GifEncoder.Quantize(palette, frame.Bgra, width, height), frame.DelayMs));
                }

                var gif = GifEncoder.Encode(width, height, indexed, palette, loopCount: 0);

                LastSize = new Size(width, height);

                lock (_gate)
                {
                    _state = RecordState.Idle;
                }

                Log.Information(
                    "[gif] 编码完成：{Frames} 帧 {Width}x{Height} · {Colors}色 · {Size} KB（{Ms} ms）",
                    count, width, height, palette.ColorCount, gif.Length / 1024, watch.ElapsedMilliseconds);

                Finished?.Invoke(gif);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _state = RecordState.Idle;
                }

                Log.Error(ex, "[gif] 编码失败");
                Failed?.Invoke("GIF 生成失败：" + ex.Message);
            }
        });
    }

    public void Dispose()
    {
        Cancel();

        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
        }

        if (thread is not null && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}

/// <summary>
/// 把鼠标指针画进帧里。
///
/// 每帧的顺序：GetCursorInfo 拿当前光标 + 屏幕位置（GDI 的 CopyFromScreen 不包含指针），
/// CopyIcon 拷一份句柄（系统随时可能把光标换掉），GetIconInfo 拿热区，
/// 再把图标画到「屏幕位置 − 热区」处 —— 指针尖端就正好落在用户实际点的那个像素上。
///
/// 几点细节：
///   * 坐标全程是物理像素（进程是 PerMonitorV2），多屏的负坐标原样代进去，
///     换算成区域坐标只是减一个 region.X/Y；
///   * 图标按自身位图尺寸（32×32 的小光标、48×48 的大光标）绘制，不缩放，热区不用跟着换算；
///   * 光标整块都在录制区域外就不画，压到边上的部分由 GDI 自己裁；
///   * GetIconInfo 交出来的两张位图、CopyIcon 拷出来的句柄都得自己释放，否则每帧漏一点 GDI 句柄。
/// </summary>
internal static class GifCursorPainter
{
    private const int CursorShowing = 0x0001;
    private const int DiNormal = 0x0003;
    private const int SmCxCursor = 13;
    private const int SmCyCursor = 14;

    /// <summary>失败过一次就不再试（远程桌面 / 无头会话里这些 API 会一直失败，别每帧刷日志）。</summary>
    private static bool _unsupported;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Handle;
        public Point Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    /// <summary>GDI BITMAP：只用它读宽高（图标位图的真实像素尺寸）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GdiBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPerPixel;
        public IntPtr Bits;
    }

    /// <summary>把当前指针画到 <paramref name="graphics"/> 上（对应屏幕区域 <paramref name="region"/>，物理像素）。</summary>
    public static void Draw(Graphics graphics, Rectangle region)
    {
        if (_unsupported)
        {
            return;
        }

        try
        {
            DrawCore(graphics, region);
        }
        catch (Exception ex)
        {
            _unsupported = true;
            Log.Debug(ex, "[gif] 画鼠标指针失败，后续帧不再尝试");
        }
    }

    private static void DrawCore(Graphics graphics, Rectangle region)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Handle == IntPtr.Zero)
        {
            // 光标没在显示（正在换光标、触屏操作、已移出所有屏幕）
            return;
        }

        // GetCursorPos 比 CursorInfo 里的位置更跟手（拖动窗口时光标会被系统夹住，这里以实际位置为准）
        if (!GetCursorPos(out var position))
        {
            position = info.Position;
        }

        var cursor = CopyIcon(info.Handle);
        if (cursor == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!GetIconInfo(cursor, out var icon))
            {
                return;
            }

            try
            {
                var (width, height) = MeasureIcon(icon);

                // 热区是图标内部坐标：减掉它，图标左上角就落在「光标位置」的左上侧
                var left = position.X - icon.HotspotX;
                var top = position.Y - icon.HotspotY;
                var bounds = new Rectangle(left, top, width, height);

                if (!bounds.IntersectsWith(region))
                {
                    return;
                }

                var hdc = graphics.GetHdc();
                try
                {
                    DrawIconEx(
                        hdc,
                        bounds.X - region.X,
                        bounds.Y - region.Y,
                        cursor,
                        width,
                        height,
                        0,
                        IntPtr.Zero,
                        DiNormal);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            finally
            {
                // GetIconInfo 把这两张位图的所有权交了出来，必须自己删
                if (icon.MaskBitmap != IntPtr.Zero)
                {
                    DeleteObject(icon.MaskBitmap);
                }

                if (icon.ColorBitmap != IntPtr.Zero)
                {
                    DeleteObject(icon.ColorBitmap);
                }
            }
        }
        finally
        {
            DestroyIcon(cursor);
        }
    }

    /// <summary>图标位图的真实像素尺寸（48×48 大光标也是它自己的尺寸，画的时候不缩放）。</summary>
    private static (int Width, int Height) MeasureIcon(in IconInfo icon)
    {
        if (icon.ColorBitmap != IntPtr.Zero
            && GetObject(icon.ColorBitmap, Marshal.SizeOf<GdiBitmap>(), out var color) != 0
            && color.Width > 0 && color.Height > 0)
        {
            return (color.Width, color.Height);
        }

        // 单色光标只有掩码位图：高度是图标的两倍（上半 AND 层、下半 XOR 层）
        if (icon.MaskBitmap != IntPtr.Zero
            && GetObject(icon.MaskBitmap, Marshal.SizeOf<GdiBitmap>(), out var mask) != 0
            && mask.Width > 0 && mask.Height > 0)
        {
            return (mask.Width, Math.Max(1, mask.Height / 2));
        }

        // 兜底：系统光标尺寸（已经按当前 DPI 缩放过）
        return (Math.Max(1, GetSystemMetrics(SmCxCursor)), Math.Max(1, GetSystemMetrics(SmCyCursor)));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
        IntPtr hdc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr flickerFreeBrush, int flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
    private static extern int GetObject(IntPtr handle, int size, out GdiBitmap bitmap);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
