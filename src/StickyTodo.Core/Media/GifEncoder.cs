using System.Text;

namespace StickyTodo.Core.Media;

/// <summary>
/// 一帧原始画面：BGRA32（字节序 B,G,R,A），自上而下，行宽 = <see cref="Width"/> * 4。
/// <see cref="DelayMs"/> 是这一帧在 GIF 里停留的毫秒数（编码时折算成 1/100 秒）。
/// </summary>
public sealed record GifFrame(byte[] Bgra, int Width, int Height, int DelayMs);

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
/// 没有做区域增量编码（每帧都是整幅），换来的是编码逻辑简单、任何解码器都吃得下。
/// </summary>
public static class GifEncoder
{
    private const int MaxCode = 4096;

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
    /// 编码成 GIF89a 字节流。<paramref name="loopCount"/> 0 = 无限循环。
    /// </summary>
    public static byte[] Encode(
        int width,
        int height,
        IReadOnlyList<(byte[] Indices, int DelayMs)> frames,
        GifPalette palette,
        int loopCount = 0)
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

        var output = new List<byte>(expected * frames.Count / 4 + 1024);

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

        foreach (var (indices, delayMs) in frames)
        {
            // ---- 图形控制扩展：disposal=1（不清除），delay 单位 1/100 秒 ----
            output.Add(0x21);
            output.Add(0xF9);
            output.Add(4);
            output.Add(0x04); // disposal=1 << 2，无透明色、无需用户输入
            AddShort(output, Math.Clamp((int)Math.Round(delayMs / 10d), 1, 65535));
            output.Add(0); // 透明色索引（未启用）
            output.Add(0);

            // ---- 图像描述符：整幅、无局部色表、不隔行 ----
            output.Add(0x2C);
            AddShort(output, 0);
            AddShort(output, 0);
            AddShort(output, width);
            AddShort(output, height);
            output.Add(0);

            // ---- LZW ----
            output.Add((byte)minCodeSize);
            WriteSubBlocks(output, LzwCompress(indices, minCodeSize));
        }

        output.Add(0x3B);
        return output.ToArray();
    }

    /// <summary>
    /// 一把梭的重载：自己抽样建调色板 → 量化每一帧 → 编码。
    /// </summary>
    public static byte[] Encode(IReadOnlyList<GifFrame> frames, int maxColors = 256, int loopCount = 0)
    {
        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("至少要有一帧", nameof(frames));
        }

        var width = frames[0].Width;
        var height = frames[0].Height;

        foreach (var frame in frames)
        {
            if (frame.Width != width || frame.Height != height)
            {
                throw new ArgumentException("所有帧的尺寸必须一致", nameof(frames));
            }
        }

        var samples = SampleFrames(frames, 12);
        var palette = BuildPalette(samples, maxColors);

        var indexed = new List<(byte[] Indices, int DelayMs)>(frames.Count);
        foreach (var frame in frames)
        {
            indexed.Add((palette.Quantize(frame.Bgra, width, height), Math.Max(1, frame.DelayMs)));
        }

        return Encode(width, height, indexed, palette, loopCount);
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
