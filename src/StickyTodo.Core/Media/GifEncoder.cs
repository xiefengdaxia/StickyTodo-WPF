using System.Text;

namespace StickyTodo.Core.Media;

/// <summary>
/// 一帧原始画面：BGRA32（字节序 B,G,R,A），自上而下，行宽 = <see cref="Width"/> * 4。
/// <see cref="DelayMs"/> 是这一帧在 GIF 里停留的毫秒数（编码时折算成 1/100 秒）。
/// </summary>
public sealed record GifFrame(byte[] Bgra, int Width, int Height, int DelayMs);

/// <summary>
/// 编码策略开关。默认值就是「更小体积 + 更清晰」的那一套：
/// 帧差分 + 相同帧合并 + 8/255 的脏像素剔除，抖动关着。
/// </summary>
public sealed record GifEncodeOptions
{
    /// <summary>默认策略（录屏用）。</summary>
    public static GifEncodeOptions Default { get; } = new();

    /// <summary>
    /// 帧差分 + 局部更新：相对上一帧只写变化像素的外接矩形，
    /// 未变区域沿用上一帧（disposal=1，不清屏）。
    /// </summary>
    public bool FrameDiff { get; init; } = true;

    /// <summary>连续完全相同的帧合并成一帧：延时累加到前一帧，总时长不变。</summary>
    public bool MergeIdenticalFrames { get; init; } = true;

    /// <summary>
    /// 脏像素剔除阈值（0 = 关）。与上一帧每个通道差都 ≤ 阈值的像素直接沿用上一帧的索引，
    /// 等于放宽量化误差换压缩率 —— gifsicle --lossy 的核心思路。
    /// </summary>
    public int LossyThreshold { get; init; } = 8;

    /// <summary>
    /// Floyd–Steinberg 抖动。默认关：截屏是纯色界面，误差扩散会把 LZW 的游程打碎，
    /// 体积涨好几倍、画面还发花。
    /// </summary>
    public bool Dither { get; init; }
}

/// <summary>
/// 全局调色板。颜色个数按 2 的幂补全（GIF 的全局色表只能是 2/4/8/…/256 项）。
///
/// 最近色查找不逐个像素暴力比距离（720×720 一帧 50 万像素 × 256 色 = 太慢），
/// 而是**按需建一张 15 位（RGB555）查找表**：32768 项，每项存最近的调色板索引。
/// 建表一次约 30-60ms，之后每个像素只是数组取值，量化一帧只要几毫秒。
/// </summary>
public sealed class GifPalette
{
    /// <summary>查找表位数：RGB 各取高 5 位。</summary>
    private const int LutBits = 15;

    private readonly int[] _colors;

    private readonly object _lutLock = new();

    private byte[]? _lut;

    internal GifPalette(IReadOnlyList<int> colors)
    {
        if (colors.Count == 0)
        {
            throw new ArgumentException("调色板至少要有一个颜色", nameof(colors));
        }

        _colors = new int[colors.Count];
        for (var i = 0; i < colors.Count; i++)
        {
            _colors[i] = colors[i] & 0xFFFFFF;
        }

        // GIF 的全局色表大小必须是 2 的幂；不足的部分补黑色
        TableSize = NextPowerOfTwo(Math.Max(2, _colors.Length));
    }

    /// <summary>实际用到的颜色数。</summary>
    public int ColorCount => _colors.Length;

    /// <summary>全局色表项数（2 的幂）。</summary>
    public int TableSize { get; }

    /// <summary>每像素索引需要的位数（色表是 2^n 项时为 n；2 色时至少 1）。</summary>
    public int BitsPerPixel
    {
        get
        {
            var bits = 1;
            while ((1 << bits) < TableSize && bits < 8)
            {
                bits++;
            }

            return bits;
        }
    }

    /// <summary>LZW 最小码长：GIF 规定不能小于 2。</summary>
    public int MinCodeSize => Math.Max(2, BitsPerPixel);

    /// <summary>RGB 三元组形式的全局色表（长度 = TableSize * 3，未用到的项为 0）。</summary>
    public byte[] ToColorTable()
    {
        var table = new byte[TableSize * 3];

        for (var i = 0; i < _colors.Length && i < TableSize; i++)
        {
            table[i * 3] = (byte)((_colors[i] >> 16) & 0xFF);
            table[i * 3 + 1] = (byte)((_colors[i] >> 8) & 0xFF);
            table[i * 3 + 2] = (byte)(_colors[i] & 0xFF);
        }

        return table;
    }

    /// <summary>
    /// 把一帧 BGRA 量化成调色板索引。返回长度 = width * height 的字节数组。
    /// </summary>
    public byte[] Quantize(byte[] bgra, int width, int height)
    {
        if (bgra is null)
        {
            throw new ArgumentNullException(nameof(bgra));
        }

        var pixels = width * height;
        if (width <= 0 || height <= 0 || bgra.Length < pixels * 4)
        {
            throw new ArgumentException("BGRA 数据长度与宽高不匹配", nameof(bgra));
        }

        var lut = EnsureLut();
        var indices = new byte[pixels];

        for (var i = 0; i < pixels; i++)
        {
            var offset = i * 4;
            var key = ((bgra[offset + 2] >> 3) << 10) | ((bgra[offset + 1] >> 3) << 5) | (bgra[offset] >> 3);
            indices[i] = lut[key];
        }

        return indices;
    }

    /// <summary>
    /// 带脏像素剔除的量化：与上一帧每个通道差都不超过 <paramref name="lossyThreshold"/> 的像素，
    /// 直接沿用上一帧的调色板索引。省下的不只是量化时间，更是差分矩形里成片的「变化」像素
    /// （录屏里的抗锯齿边缘、半透明阴影每帧都会轻微抖一点）。
    /// </summary>
    internal byte[] QuantizeWithLossy(byte[] bgra, byte[] previousBgra, byte[] previousIndices, int lossyThreshold)
    {
        var pixels = previousIndices.Length;
        Validate(bgra, pixels, nameof(bgra));

        if (previousBgra.Length < pixels * 4)
        {
            throw new ArgumentException("上一帧 BGRA 数据长度与宽高不匹配", nameof(previousBgra));
        }

        var lut = EnsureLut();
        var indices = new byte[pixels];
        var threshold = Math.Clamp(lossyThreshold, 0, 255);

        for (var i = 0; i < pixels; i++)
        {
            var offset = i * 4;

            var db = bgra[offset] - previousBgra[offset];
            var dg = bgra[offset + 1] - previousBgra[offset + 1];
            var dr = bgra[offset + 2] - previousBgra[offset + 2];

            if (db <= threshold && db >= -threshold &&
                dg <= threshold && dg >= -threshold &&
                dr <= threshold && dr >= -threshold)
            {
                // 肉眼看不出来的一点点变化：沿用上一帧的颜色，压缩率优先
                indices[i] = previousIndices[i];
                continue;
            }

            var key = ((bgra[offset + 2] >> 3) << 10) | ((bgra[offset + 1] >> 3) << 5) | (bgra[offset] >> 3);
            indices[i] = lut[key];
        }

        return indices;
    }

    /// <summary>
    /// Floyd–Steinberg 误差扩散量化。**默认不用**：纯色界面上抖动只会把游程打碎、体积翻几倍。
    /// 只有渐变 / 照片类画面才值得开。
    /// </summary>
    public byte[] QuantizeDithered(byte[] bgra, int width, int height)
    {
        var pixels = width * height;
        Validate(bgra, pixels, nameof(bgra));

        var lut = EnsureLut();
        var indices = new byte[pixels];

        // 误差按 1/16 为单位累加在两个 Int 缓冲里（当前行 / 下一行），避免逐像素分配
        var current = new int[(width + 2) * 3];
        var next = new int[(width + 2) * 3];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var slot = (x + 1) * 3;

                var b = ClampByte(bgra[offset] + current[slot]);
                var g = ClampByte(bgra[offset + 1] + current[slot + 1]);
                var r = ClampByte(bgra[offset + 2] + current[slot + 2]);

                var index = lut[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];
                indices[y * width + x] = index;

                var color = _colors[index];
                var eb = b - (color & 0xFF);
                var eg = g - ((color >> 8) & 0xFF);
                var er = r - ((color >> 16) & 0xFF);

                if (eb == 0 && eg == 0 && er == 0)
                {
                    continue;
                }

                // 7/16 右、3/16 左下、5/16 下、1/16 右下
                current[(x + 2) * 3] += eb * 7 / 16;
                current[(x + 2) * 3 + 1] += eg * 7 / 16;
                current[(x + 2) * 3 + 2] += er * 7 / 16;

                next[(x) * 3] += eb * 3 / 16;
                next[(x) * 3 + 1] += eg * 3 / 16;
                next[(x) * 3 + 2] += er * 3 / 16;

                next[(x + 1) * 3] += eb * 5 / 16;
                next[(x + 1) * 3 + 1] += eg * 5 / 16;
                next[(x + 1) * 3 + 2] += er * 5 / 16;

                next[(x + 2) * 3] += eb / 16;
                next[(x + 2) * 3 + 1] += eg / 16;
                next[(x + 2) * 3 + 2] += er / 16;
            }

            Array.Clear(current, 0, current.Length);
            (current, next) = (next, current);
        }

        return indices;
    }

    private static byte ClampByte(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;

    private static void Validate(byte[] bgra, int pixels, string name)
    {
        if (bgra is null)
        {
            throw new ArgumentNullException(name);
        }

        if (bgra.Length < pixels * 4)
        {
            throw new ArgumentException("BGRA 数据长度与宽高不匹配", name);
        }
    }

    /// <summary>最近色索引（精确 RGB 查找，会建/复用缓存表）。</summary>
    public byte NearestIndex(byte r, byte g, byte b) =>
        EnsureLut()[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];

    /// <summary>按需建 15 位查找表（多线程安全：只在第一次用的时候建一次）。</summary>
    private byte[] EnsureLut()
    {
        var existing = _lut;
        if (existing is not null)
        {
            return existing;
        }

        lock (_lutLock)
        {
            if (_lut is not null)
            {
                return _lut;
            }

            var lut = new byte[1 << LutBits];

            for (var key = 0; key < lut.Length; key++)
            {
                // 5 位 → 8 位：乘 255/31 展开，比左移 3 位（最高只到 248）更贴近真实色
                var r = ((key >> 10) & 0x1F) * 255 / 31;
                var g = ((key >> 5) & 0x1F) * 255 / 31;
                var b = (key & 0x1F) * 255 / 31;

                lut[key] = FindNearest(r, g, b);
            }

            _lut = lut;
            return lut;
        }
    }

    private byte FindNearest(int r, int g, int b)
    {
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < _colors.Length; i++)
        {
            var color = _colors[i];
            var dr = r - ((color >> 16) & 0xFF);
            var dg = g - ((color >> 8) & 0xFF);
            var db = b - (color & 0xFF);

            // 人眼对绿更敏感，权重略大
            var distance = dr * dr * 3 + dg * dg * 6 + db * db * 1;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;

                if (distance == 0)
                {
                    break;
                }
            }
        }

        return (byte)best;
    }

    internal static int NextPowerOfTwo(int value)
    {
        var size = 2;
        while (size < value && size < 256)
        {
            size <<= 1;
        }

        return size;
    }
}

/// <summary>
/// 纯托管的 GIF89a 编码器（不引第三方包，net6.0 / net8.0 都能编译）。
///
/// 输出结构：
///   "GIF89a" → 逻辑屏描述符 + 全局色表 → NETSCAPE2.0 循环块
///   → 每帧 [图形控制扩展(disposal=1, delay=1/100s) + 图像描述符 + LZW 数据]
///   → 0x3B 结束符
///
/// 省体积的三招（都是 --lossy / gifski 那一套路子，纯托管实现）：
///   1. 帧差分 + 局部更新：只写变化像素的外接矩形，未变区域靠 disposal=1 留在屏幕上；
///   2. 相同帧合并：连续一模一样的帧不写图，延时累加到前一帧；
///   3. 脏像素剔除 + 关抖动：量化前把「和上一帧差不到 8/255」的像素沿上一帧的索引，
///      纯色界面不做误差扩散（抖动会毁掉 LZW 的游程）。
/// 解出来的每一帧都和「整幅全写」完全一致 —— 只是重复的像素不再重传。
/// </summary>
public static class GifEncoder
{
    private const int MaxCode = 4096;

    /// <summary>单个帧延时的上限（GIF 是 16 位、单位 1/100 秒）。相同帧合并到这个数就不再累加。</summary>
    private const int MaxDelayMs = 655350;

    /// <summary>变化面积占整幅的比例超过这个值就退回整帧（%）。</summary>
    private const int FrameDiffMaxPercent = 90;

    /// <summary>
    /// 用抽样帧建调色板：中位切分（median cut）把颜色空间切成 maxColors 个盒子，
    /// 每个盒子取平均色。颜色数会向上补到 2 的幂。
    /// </summary>
    public static GifPalette BuildPalette(IReadOnlyList<GifFrame> samples, int maxColors)
    {
        if (samples is null || samples.Count == 0)
        {
            throw new ArgumentException("至少需要一帧采样", nameof(samples));
        }

        var target = GifPalette.NextPowerOfTwo(Math.Clamp(maxColors, 2, 256));
        var pixels = CollectSamples(samples, maxPixels: 60000);

        if (pixels.Length == 0)
        {
            throw new ArgumentException("采样帧里没有像素", nameof(samples));
        }

        var boxes = new List<PaletteBox> { new(pixels, 0, pixels.Length) };

        while (boxes.Count < target)
        {
            var index = PickBoxToSplit(boxes);
            if (index < 0)
            {
                break;
            }

            var (first, second) = boxes[index].Split(pixels);
            if (second is null)
            {
                break;
            }

            boxes[index] = first;
            boxes.Add(second);
        }

        var colors = new List<int>(boxes.Count);
        foreach (var box in boxes)
        {
            colors.Add(box.Average(pixels));
        }

        return new GifPalette(colors);
    }

    /// <summary>把一帧 BGRA 量化成调色板索引。</summary>
    public static byte[] Quantize(GifPalette palette, byte[] bgra, int width, int height)
    {
        if (palette is null)
        {
            throw new ArgumentNullException(nameof(palette));
        }

        return palette.Quantize(bgra, width, height);
    }

    /// <summary>
    /// 编码成 GIF89a 字节流。<paramref name="loopCount"/> 0 = 无限循环；
    /// <paramref name="options"/> 为 null 时走 <see cref="GifEncodeOptions.Default"/>。
    /// </summary>
    public static byte[] Encode(
        int width,
        int height,
        IReadOnlyList<(byte[] Indices, int DelayMs)> frames,
        GifPalette palette,
        int loopCount = 0,
        GifEncodeOptions? options = null)
    {
        if (palette is null)
        {
            throw new ArgumentNullException(nameof(palette));
        }

        if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "GIF 尺寸必须在 1..65535 之间");
        }

        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("至少要有一帧", nameof(frames));
        }

        var expected = width * height;
        foreach (var frame in frames)
        {
            if (frame.Indices is null || frame.Indices.Length != expected)
            {
                throw new ArgumentException("帧索引数与宽高不匹配", nameof(frames));
            }
        }

        var effective = options ?? GifEncodeOptions.Default;
        var plan = BuildPlan(
            width,
            height,
            frames.Count,
            i => frames[i].Indices,
            i => frames[i].DelayMs,
            effective);

        return Write(width, height, plan, palette, loopCount);
    }

    /// <summary>
    /// 用现成的调色板编码一组 BGRA 帧：量化（含脏像素剔除）→ 帧差分 / 相同帧合并 → 写文件。
    /// 逐帧量化、量化完就扔，不像旧版那样把 300 帧的索引数组全留在内存里。
    /// </summary>
    public static byte[] Encode(
        IReadOnlyList<GifFrame> frames,
        GifPalette palette,
        int loopCount = 0,
        GifEncodeOptions? options = null)
    {
        if (palette is null)
        {
            throw new ArgumentNullException(nameof(palette));
        }

        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("至少要有一帧", nameof(frames));
        }

        var width = frames[0].Width;
        var height = frames[0].Height;
        EnsureSameSize(frames, width, height);

        var effective = options ?? GifEncodeOptions.Default;
        byte[]? previousBgra = null;
        byte[]? previousIndices = null;

        var plan = BuildPlan(
            width,
            height,
            frames.Count,
            i =>
            {
                var frame = frames[i];
                byte[] indices;

                if (effective.Dither)
                {
                    indices = palette.QuantizeDithered(frame.Bgra, width, height);
                }
                else if (previousBgra is not null && previousIndices is not null && effective.LossyThreshold > 0)
                {
                    indices = palette.QuantizeWithLossy(
                        frame.Bgra, previousBgra, previousIndices, effective.LossyThreshold);
                }
                else
                {
                    indices = palette.Quantize(frame.Bgra, width, height);
                }

                previousBgra = frame.Bgra;
                previousIndices = indices;
                return indices;
            },
            i => frames[i].DelayMs,
            effective);

        return Write(width, height, plan, palette, loopCount);
    }

    /// <summary>
    /// 一把梭的重载：自己抽样建调色板 → 逐帧量化 + 帧差分 → 编码。
    /// </summary>
    public static byte[] Encode(
        IReadOnlyList<GifFrame> frames,
        int maxColors = 256,
        int loopCount = 0,
        GifEncodeOptions? options = null)
    {
        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("至少要有一帧", nameof(frames));
        }

        var width = frames[0].Width;
        var height = frames[0].Height;
        EnsureSameSize(frames, width, height);

        var samples = SampleFrames(frames, 12);
        var palette = BuildPalette(samples, maxColors);

        return Encode(frames, palette, loopCount, options);
    }

    /// <summary>
    /// 把「整幅索引帧」压成「实际要写进文件的帧」：合并相同帧、算变化矩形。
    /// 逐帧取用（<paramref name="indicesOf"/>），量化完就能被回收。
    /// </summary>
    private static List<PlannedFrame> BuildPlan(
        int width,
        int height,
        int count,
        Func<int, byte[]> indicesOf,
        Func<int, int> delayOf,
        GifEncodeOptions options)
    {
        var plan = new List<PlannedFrame>(count);
        var canvas = new byte[width * height];
        var full = new PixelRect(0, 0, width, height);
        var total = width * height;
        var hasCanvas = false;

        for (var i = 0; i < count; i++)
        {
            var indices = indicesOf(i);
            var delay = Math.Max(1, delayOf(i));

            if (!hasCanvas)
            {
                // 第一帧必须是整幅：后面所有局部更新都叠在它上面
                plan.Add(new PlannedFrame(0, 0, width, height, indices, delay));
                Buffer.BlockCopy(indices, 0, canvas, 0, total);
                hasCanvas = true;
                continue;
            }

            var rect = options.FrameDiff ? ChangedRect(canvas, width, height, indices) : full;

            if (rect.IsEmpty)
            {
                // 这一帧和上一帧一模一样：不写图，延时加到前一帧上（总时长不变）
                if (options.MergeIdenticalFrames)
                {
                    var last = plan[^1];
                    plan[^1] = last with { DelayMs = Math.Min(last.DelayMs + delay, MaxDelayMs) };
                    continue;
                }

                // 不让合并就写一个最小的块，画面本来就没变
                rect = new PixelRect(0, 0, Math.Min(4, width), Math.Min(4, height));
            }
            else if (options.FrameDiff && (long)rect.Area * 100 > (long)total * FrameDiffMaxPercent)
            {
                // 几乎整幅都变了：整帧反而比一大块外接矩形更省
                rect = full;
            }

            var block = rect.Left == 0 && rect.Top == 0 && rect.Width == width && rect.Height == height
                ? indices
                : Crop(indices, width, rect);

            plan.Add(new PlannedFrame(rect.Left, rect.Top, rect.Width, rect.Height, block, delay));
            Buffer.BlockCopy(indices, 0, canvas, 0, total);
        }

        return plan;
    }

    /// <summary>
    /// 变化像素的外接矩形：留 2px 边距（抗锯齿 / 阴影的尾巴），四像素对齐。
    /// 一整行都没变就直接跳过 —— 纯色界面上绝大多数行都是这种，比逐像素比快得多。
    /// </summary>
    private static PixelRect ChangedRect(byte[] canvas, int width, int height, byte[] indices)
    {
        const int Padding = 2;

        int minX = width, minY = height, maxX = -1, maxY = -1;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;

            if (canvas.AsSpan(row, width).SequenceEqual(indices.AsSpan(row, width)))
            {
                continue;
            }

            if (y < minY)
            {
                minY = y;
            }

            maxY = y;

            for (var x = 0; x < width; x++)
            {
                if (canvas[row + x] == indices[row + x])
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }
            }
        }

        if (maxX < 0)
        {
            return default;
        }

        var left = (Math.Max(0, minX - Padding)) & ~3;
        var top = (Math.Max(0, minY - Padding)) & ~3;
        var right = Math.Min(width, (maxX + 1 + Padding + 3) & ~3);
        var bottom = Math.Min(height, (maxY + 1 + Padding + 3) & ~3);

        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static byte[] Crop(byte[] indices, int width, PixelRect rect)
    {
        var block = new byte[rect.Width * rect.Height];

        for (var y = 0; y < rect.Height; y++)
        {
            Buffer.BlockCopy(indices, (rect.Top + y) * width + rect.Left, block, y * rect.Width, rect.Width);
        }

        return block;
    }

    private static void EnsureSameSize(IReadOnlyList<GifFrame> frames, int width, int height)
    {
        foreach (var frame in frames)
        {
            if (frame.Width != width || frame.Height != height)
            {
                throw new ArgumentException("所有帧的尺寸必须一致", nameof(frames));
            }
        }
    }

    /// <summary>把算好的帧写进 GIF89a 字节流。</summary>
    private static byte[] Write(int width, int height, List<PlannedFrame> plan, GifPalette palette, int loopCount)
    {
        var output = new List<byte>(width * height / 4 + 1024);

        // ---- 文件头 + 逻辑屏描述符 ----
        output.AddRange(Encoding.ASCII.GetBytes("GIF89a"));
        AddShort(output, width);
        AddShort(output, height);

        var tableExponent = palette.BitsPerPixel - 1;
        // 全局色表(1) | 颜色深度 8 位(111) | 不排序 | 表大小指数
        output.Add((byte)(0x80 | 0x70 | (tableExponent & 0x07)));
        output.Add(0); // 背景色索引
        output.Add(0); // 像素宽高比

        output.AddRange(palette.ToColorTable());

        // ---- NETSCAPE2.0 循环块（0 = 无限循环）----
        if (loopCount >= 0)
        {
            output.Add(0x21);
            output.Add(0xFF);
            output.Add(11);
            output.AddRange(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            output.Add(3);
            output.Add(1);
            AddShort(output, Math.Min(loopCount, 65535));
            output.Add(0);
        }

        var minCodeSize = palette.MinCodeSize;

        foreach (var frame in plan)
        {
            // ---- 图形控制扩展：disposal=1（不清除），delay 单位 1/100 秒 ----
            output.Add(0x21);
            output.Add(0xF9);
            output.Add(4);
            output.Add(0x04); // disposal=1 << 2，无透明色、无需用户输入
            AddShort(output, Math.Clamp((int)Math.Round(frame.DelayMs / 10d), 1, 65535));
            output.Add(0); // 透明色索引（未启用）
            output.Add(0);

            // ---- 图像描述符：可能是整幅，也可能只是一块局部区域 ----
            output.Add(0x2C);
            AddShort(output, frame.Left);
            AddShort(output, frame.Top);
            AddShort(output, frame.Width);
            AddShort(output, frame.Height);
            output.Add(0);

            // ---- LZW ----
            output.Add((byte)minCodeSize);
            WriteSubBlocks(output, LzwCompress(frame.Indices, minCodeSize));
        }

        output.Add(0x3B);
        return output.ToArray();
    }

    /// <summary>一帧最终要写进文件的样子：局部矩形的索引 + 帧延时。</summary>
    private readonly record struct PlannedFrame(int Left, int Top, int Width, int Height, byte[] Indices, int DelayMs);

    /// <summary>像素矩形（不引 System.Drawing，编码器保持纯托管 + 跨目标框架）。</summary>
    private readonly record struct PixelRect(int Left, int Top, int Width, int Height)
    {
        public int Area => Width * Height;

        public bool IsEmpty => Width <= 0 || Height <= 0;
    }

    /// <summary>帧里均匀抽若干个采样（建调色板用，别拿 300 帧全量算）。</summary>
    public static List<GifFrame> SampleFrames(IReadOnlyList<GifFrame> frames, int count)
    {
        var result = new List<GifFrame>();
        if (frames.Count == 0)
        {
            return result;
        }

        count = Math.Clamp(count, 1, frames.Count);
        if (frames.Count <= count)
        {
            result.AddRange(frames);
            return result;
        }

        for (var i = 0; i < count; i++)
        {
            var index = (int)((long)i * (frames.Count - 1) / Math.Max(1, count - 1));
            result.Add(frames[index]);
        }

        return result;
    }

    // ---------------- 采样 / 中位切分 ----------------

    private static int[] CollectSamples(IReadOnlyList<GifFrame> frames, int maxPixels)
    {
        long totalPixels = 0;
        foreach (var frame in frames)
        {
            totalPixels += (long)Math.Max(0, frame.Width) * Math.Max(0, frame.Height);
        }

        if (totalPixels == 0)
        {
            return Array.Empty<int>();
        }

        // 隔多少个像素取一个：像素越多间隔越大，采样总量控制在 maxPixels 附近
        var step = Math.Max(1, totalPixels / Math.Max(1, maxPixels));
        var samples = new List<int>((int)Math.Min(totalPixels, maxPixels) + 8);

        foreach (var frame in frames)
        {
            var bgra = frame.Bgra;
            var pixels = frame.Width * frame.Height;

            for (long i = 0; i < pixels; i += step)
            {
                var offset = (int)i * 4;
                if (offset + 3 >= bgra.Length)
                {
                    break;
                }

                samples.Add((bgra[offset + 2] << 16) | (bgra[offset + 1] << 8) | bgra[offset]);
            }
        }

        return samples.ToArray();
    }

    private static int PickBoxToSplit(List<PaletteBox> boxes)
    {
        var best = -1;
        long bestScore = 0;

        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (box.Count < 2 || box.MaxRange <= 0)
            {
                continue;
            }

            // 范围越宽、像素越多的盒子越值得再切一刀
            var score = (long)box.MaxRange * box.Count;
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    /// <summary>中位切分用的「颜色盒子」：int[] 里 [Start, Start+Count) 这一段。</summary>
    private sealed class PaletteBox
    {
        private readonly byte _minR;
        private readonly byte _minG;
        private readonly byte _minB;
        private readonly byte _maxR;
        private readonly byte _maxG;
        private readonly byte _maxB;

        public PaletteBox(int[] pixels, int start, int count)
        {
            Start = start;
            Count = count;

            if (count <= 0)
            {
                return;
            }

            byte minR = 255, minG = 255, minB = 255;
            byte maxR = 0, maxG = 0, maxB = 0;

            for (var i = start; i < start + count; i++)
            {
                var color = pixels[i];
                var r = (byte)((color >> 16) & 0xFF);
                var g = (byte)((color >> 8) & 0xFF);
                var b = (byte)(color & 0xFF);

                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                if (g < minG) minG = g;
                if (g > maxG) maxG = g;
                if (b < minB) minB = b;
                if (b > maxB) maxB = b;
            }

            _minR = minR;
            _minG = minG;
            _minB = minB;
            _maxR = maxR;
            _maxG = maxG;
            _maxB = maxB;
        }

        public int Start { get; }

        public int Count { get; }

        public int MaxRange => Math.Max(_maxR - _minR, Math.Max(_maxG - _minG, _maxB - _minB));

        private int WidestChannel
        {
            get
            {
                var rr = _maxR - _minR;
                var rg = _maxG - _minG;
                var rb = _maxB - _minB;

                if (rg >= rr && rg >= rb)
                {
                    return 1;
                }

                return rb >= rr ? 2 : 0;
            }
        }

        /// <summary>沿着最宽通道排序后从中间切开。切不动时 second 为 null。</summary>
        public (PaletteBox First, PaletteBox? Second) Split(int[] pixels)
        {
            if (Count < 2)
            {
                return (this, null);
            }

            var channel = WidestChannel;
            Array.Sort(pixels, Start, Count, new ChannelComparer(channel));

            var split = FindSplitPoint(
                pixels,
                Start,
                Count,
                channel switch { 0 => 16, 1 => 8, _ => 0 },
                Start + Count / 2);

            if (split <= Start || split >= Start + Count)
            {
                return (this, null);
            }

            var first = new PaletteBox(pixels, Start, split - Start);
            var second = new PaletteBox(pixels, split, Start + Count - split);
            return (first, second);
        }

        /// <summary>
        /// 从中间往两边找最近的「通道值分界」再切。
        /// 直接在像素中位处切会把同一个颜色劈到两个盒子里，两边的平均色都变成没人用的中间色，
        /// 界面上的纯色（白底、标题栏、图标）就会糊成一片 —— 这是 GIF 录屏最显眼的画质问题。
        /// </summary>
        private static int FindSplitPoint(int[] pixels, int start, int count, int shift, int mid)
        {
            var end = start + count;

            for (var delta = 0; delta < count; delta++)
            {
                var forward = mid + delta;
                if (forward > start && forward < end &&
                    ((pixels[forward] >> shift) & 0xFF) != ((pixels[forward - 1] >> shift) & 0xFF))
                {
                    return forward;
                }

                var backward = mid - delta;
                if (backward > start && backward < end &&
                    ((pixels[backward] >> shift) & 0xFF) != ((pixels[backward - 1] >> shift) & 0xFF))
                {
                    return backward;
                }
            }

            return start;
        }

        public int Average(int[] pixels)
        {
            if (Count <= 0)
            {
                return 0;
            }

            long r = 0, g = 0, b = 0;
            for (var i = Start; i < Start + Count; i++)
            {
                var color = pixels[i];
                r += (color >> 16) & 0xFF;
                g += (color >> 8) & 0xFF;
                b += color & 0xFF;
            }

            return (int)(r / Count) << 16 | (int)(g / Count) << 8 | (int)(b / Count);
        }
    }

    private sealed class ChannelComparer : IComparer<int>
    {
        private readonly int _shift;

        public ChannelComparer(int channel) => _shift = channel switch
        {
            0 => 16,
            1 => 8,
            _ => 0,
        };

        public int Compare(int x, int y) => ((x >> _shift) & 0xFF) - ((y >> _shift) & 0xFF);
    }

    // ---------------- LZW ----------------

    /// <summary>
    /// GIF 的 LZW（可变码长、字典满 4096 就发 ClearCode 重来）。
    /// 码长增长时机与参考实现 GIFCOMPR 一致：写完一个码之后，若下一个待分配码
    /// 超出当前码宽能表示的范围，就把码长 +1（最大 12 位）。
    /// </summary>
    private static byte[] LzwCompress(byte[] indices, int minCodeSize)
    {
        var clearCode = 1 << minCodeSize;
        var endCode = clearCode + 1;
        var codeSize = minCodeSize + 1;
        var nextCode = endCode + 1;

        var dictionary = new Dictionary<int, int>(4096);
        var writer = new BitWriter();
        writer.Write(clearCode, codeSize);

        if (indices.Length == 0)
        {
            writer.Write(endCode, codeSize);
            return writer.Finish();
        }

        var prefix = (int)indices[0];

        for (var i = 1; i < indices.Length; i++)
        {
            var suffix = (int)indices[i];
            var key = (prefix << 8) | suffix;

            if (dictionary.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }

            writer.Write(prefix, codeSize);

            if (nextCode == MaxCode)
            {
                // 字典满：发一次 ClearCode 让解码端一起重置
                writer.Write(clearCode, codeSize);
                dictionary.Clear();
                codeSize = minCodeSize + 1;
                nextCode = endCode + 1;
            }
            else
            {
                dictionary[key] = nextCode++;

                if (nextCode > (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }

            prefix = suffix;
        }

        writer.Write(prefix, codeSize);
        writer.Write(endCode, codeSize);
        return writer.Finish();
    }

    /// <summary>LSB 优先的位打包器。</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new(1024);
        private int _accumulator;
        private int _bits;

        public void Write(int code, int bitCount)
        {
            _accumulator |= (code & ((1 << bitCount) - 1)) << _bits;
            _bits += bitCount;

            while (_bits >= 8)
            {
                _bytes.Add((byte)(_accumulator & 0xFF));
                _accumulator >>= 8;
                _bits -= 8;
            }
        }

        public byte[] Finish()
        {
            if (_bits > 0)
            {
                _bytes.Add((byte)(_accumulator & 0xFF));
                _accumulator = 0;
                _bits = 0;
            }

            return _bytes.ToArray();
        }
    }

    /// <summary>把 LZW 字节流切成 ≤255 字节的子块（每块前面一个长度字节），最后以 0 结尾。</summary>
    private static void WriteSubBlocks(List<byte> output, byte[] data)
    {
        var offset = 0;

        while (offset < data.Length)
        {
            var chunk = Math.Min(255, data.Length - offset);
            output.Add((byte)chunk);
            output.AddRange(new ArraySegment<byte>(data, offset, chunk));
            offset += chunk;
        }

        output.Add(0);
    }

    private static void AddShort(List<byte> output, int value)
    {
        output.Add((byte)(value & 0xFF));
        output.Add((byte)((value >> 8) & 0xFF));
    }
}

/// <summary>
/// 纯托管的 BGRA 缩放：Lanczos3（缩小的时候按比例放宽窗口，等于自带抗锯齿）+ 小半径反锐化。
///
/// 缩小录屏时文字最怕糊：双线性会把笔画边缘抹成一片灰，Lanczos3 保得住边缘对比，
/// 缩完再补一点 unsharp 把被平均掉的笔画找回来 —— 同样是那么大体积，看着更清楚。
/// 只在真的要缩的时候用（不缩就不必付这个开销）。
/// </summary>
public static class GifResampler
{
    /// <summary>Lanczos 的半径（3 = Lanczos3，业界公认的体积/锐度平衡点）。</summary>
    private const int KernelRadius = 3;

    /// <summary>unsharp 的默认强度。</summary>
    public const double DefaultSharpen = 0.6;

    /// <summary>
    /// 把 <paramref name="src"/>（BGRA32、行宽 = 宽 × 4）缩放到目标尺寸。
    /// <paramref name="sharpen"/> 为 0 表示不做反锐化；尺寸相同则原样返回。
    /// </summary>
    public static byte[] Resize(
        byte[] src,
        int srcWidth,
        int srcHeight,
        int dstWidth,
        int dstHeight,
        double sharpen = DefaultSharpen)
    {
        if (src is null)
        {
            throw new ArgumentNullException(nameof(src));
        }

        if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "缩放尺寸必须大于 0");
        }

        if (src.Length < srcWidth * srcHeight * 4)
        {
            throw new ArgumentException("BGRA 数据长度与宽高不匹配", nameof(src));
        }

        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            var same = (byte[])src.Clone();
            if (sharpen > 0)
            {
                Unsharp(same, dstWidth, dstHeight, sharpen);
            }

            return same;
        }

        var horizontal = ResizeHorizontal(src, srcWidth, srcHeight, dstWidth);
        var result = ResizeVertical(horizontal, dstWidth, srcHeight, dstHeight);

        if (sharpen > 0)
        {
            Unsharp(result, dstWidth, dstHeight, sharpen);
        }

        return result;
    }

    /// <summary>按目标长度建一张「起点 + 归一化权重」的采样表（每个目标像素只算一次）。</summary>
    private static (int Start, float[] Weights)[] BuildTaps(int srcLength, int dstLength)
    {
        var scale = (double)srcLength / dstLength;
        var stretch = Math.Max(1d, scale);
        var support = KernelRadius * stretch;
        var taps = new (int Start, float[] Weights)[dstLength];

        for (var i = 0; i < dstLength; i++)
        {
            var center = ((i + 0.5) * scale) - 0.5;
            var start = (int)Math.Floor(center - support);
            var end = (int)Math.Ceiling(center + support);

            if (start < 0)
            {
                start = 0;
            }

            if (end > srcLength - 1)
            {
                end = srcLength - 1;
            }

            var weights = new float[end - start + 1];
            double sum = 0;

            for (var k = 0; k < weights.Length; k++)
            {
                var distance = (start + k - center) / stretch;
                var weight = Lanczos(distance);
                weights[k] = (float)weight;
                sum += weight;
            }

            if (sum <= 0)
            {
                // 理论上不会发生（center 处权重恒为 1）；兜底成最近邻
                Array.Clear(weights, 0, weights.Length);
                weights[Math.Clamp((int)Math.Round(center), 0, weights.Length - 1)] = 1f;
            }
            else
            {
                for (var k = 0; k < weights.Length; k++)
                {
                    weights[k] = (float)(weights[k] / sum);
                }
            }

            taps[i] = (start, weights);
        }

        return taps;
    }

    private static double Lanczos(double x)
    {
        if (x == 0)
        {
            return 1;
        }

        if (x <= -KernelRadius || x >= KernelRadius)
        {
            return 0;
        }

        var px = Math.PI * x;
        return (KernelRadius * Math.Sin(px) * Math.Sin(px / KernelRadius)) / (px * px);
    }

    /// <summary>横向：宽 srcWidth → dstWidth，高度不变。</summary>
    private static byte[] ResizeHorizontal(byte[] src, int srcWidth, int srcHeight, int dstWidth)
    {
        var taps = BuildTaps(srcWidth, dstWidth);
        var dst = new byte[dstWidth * srcHeight * 4];
        var srcRowBytes = srcWidth * 4;
        var dstRowBytes = dstWidth * 4;

        Parallel.For(0, srcHeight, y =>
        {
            var srcRow = y * srcRowBytes;
            var dstRow = y * dstRowBytes;

            for (var x = 0; x < dstWidth; x++)
            {
                var (start, weights) = taps[x];
                float b = 0, g = 0, r = 0, a = 0;

                for (var k = 0; k < weights.Length; k++)
                {
                    var weight = weights[k];
                    var offset = srcRow + ((start + k) * 4);

                    b += src[offset] * weight;
                    g += src[offset + 1] * weight;
                    r += src[offset + 2] * weight;
                    a += src[offset + 3] * weight;
                }

                var target = dstRow + (x * 4);
                dst[target] = ToByte(b);
                dst[target + 1] = ToByte(g);
                dst[target + 2] = ToByte(r);
                dst[target + 3] = ToByte(a);
            }
        });

        return dst;
    }

    /// <summary>纵向：高 srcHeight → dstHeight，宽度不变。</summary>
    private static byte[] ResizeVertical(byte[] src, int width, int srcHeight, int dstHeight)
    {
        var taps = BuildTaps(srcHeight, dstHeight);
        var dst = new byte[width * dstHeight * 4];
        var rowBytes = width * 4;

        Parallel.For(0, dstHeight, y =>
        {
            var (start, weights) = taps[y];
            var dstRow = y * rowBytes;

            for (var x = 0; x < width; x++)
            {
                var column = x * 4;
                float b = 0, g = 0, r = 0, a = 0;

                for (var k = 0; k < weights.Length; k++)
                {
                    var weight = weights[k];
                    var offset = ((start + k) * rowBytes) + column;

                    b += src[offset] * weight;
                    g += src[offset + 1] * weight;
                    r += src[offset + 2] * weight;
                    a += src[offset + 3] * weight;
                }

                var target = dstRow + column;
                dst[target] = ToByte(b);
                dst[target + 1] = ToByte(g);
                dst[target + 2] = ToByte(r);
                dst[target + 3] = ToByte(a);
            }
        });

        return dst;
    }

    /// <summary>
    /// 小半径反锐化：[1 2 1] × [1 2 1] 的高斯模糊做参考，p + amount × (p - blur)。
    /// 半径只有 1 像素，不会出白边，只把笔画边缘的对比拉回来一点。
    /// </summary>
    private static void Unsharp(byte[] pixels, int width, int height, double amount)
    {
        if (width < 3 || height < 3)
        {
            return;
        }

        var source = (byte[])pixels.Clone();
        var rowBytes = width * 4;

        // 先横向 [1 2 1] 再纵向 [1 2 1]，等于那个 3×3 高斯核，但每个像素只读 3 个数
        var horizontal = new byte[source.Length];

        Parallel.For(0, height, y =>
        {
            var row = y * rowBytes;

            for (var x = 0; x < width; x++)
            {
                var left = Math.Max(0, x - 1) * 4;
                var center = x * 4;
                var right = Math.Min(width - 1, x + 1) * 4;

                for (var channel = 0; channel < 4; channel++)
                {
                    horizontal[row + center + channel] = (byte)(
                        (source[row + left + channel]
                         + (2 * source[row + center + channel])
                         + source[row + right + channel]) >> 2);
                }
            }
        });

        Parallel.For(0, height, y =>
        {
            var up = Math.Max(0, y - 1) * rowBytes;
            var middle = y * rowBytes;
            var down = Math.Min(height - 1, y + 1) * rowBytes;

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;

                for (var channel = 0; channel < 3; channel++)
                {
                    var blur = (horizontal[up + offset + channel]
                                + (2 * horizontal[middle + offset + channel])
                                + horizontal[down + offset + channel]) >> 2;

                    var original = source[middle + offset + channel];
                    pixels[middle + offset + channel] = ToByte(original + (int)Math.Round(amount * (original - blur)));
                }
            }
        });
    }

    private static byte ToByte(float value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(value + 0.5f);

    private static byte ToByte(int value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)value;
}
