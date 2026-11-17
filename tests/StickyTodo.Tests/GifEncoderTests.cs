using StickyTodo.Core.Media;

namespace StickyTodo.Tests;

/// <summary>
/// GIF 编码器测试：帧差分 / 相同帧合并 / 脏像素剔除 / Lanczos3 缩放。
///
/// 断言都落在「解回来的画面对不对」上，而不是只看字节数 —— 差分编码最容易犯的错
/// 就是体积小了但画面错了（局部矩形算错、disposal 设错、行对齐错）。
/// </summary>
public class GifEncoderTests
{
    private const int Width = 200;
    private const int Height = 200;

    private const int Bg = 0xF0F0F0;
    private const int Fg = 0x204080;

    // ---------------- ① 帧差分：小方块移动 ----------------

    [Fact]
    public void FrameDiff_ShrinksSmallMovingSquare_WellBelowFullFrameSize()
    {
        // 30 帧，每帧只有一个 20×20 的小方块挪 4px：变化面积约 1%（含边距也就 2%）
        var frames = BuildMovingSquareFrames(count: 30);
        var palette = GifEncoder.BuildPalette(GifEncoder.SampleFrames(frames, 12), 16);

        var diffed = GifEncoder.Encode(frames, palette, options: new GifEncodeOptions
        {
            FrameDiff = true,
            MergeIdenticalFrames = true,
            LossyThreshold = 0,
        });

        var fullFrame = GifEncoder.Encode(frames, palette, options: new GifEncodeOptions
        {
            FrameDiff = false,
            MergeIdenticalFrames = false,
            LossyThreshold = 0,
        });

        Assert.True(
            diffed.Length < fullFrame.Length * 0.6,
            $"帧差分后 {diffed.Length} 字节，整帧 {fullFrame.Length} 字节，没降到 60% 以下");
    }

    // ---------------- ② 相同帧合并 ----------------

    [Fact]
    public void IdenticalFrames_AreMerged_WithTotalDelayPreserved()
    {
        // 10 帧完全一样，每帧 100ms
        var frames = Enumerable.Range(0, 10)
            .Select(_ => SolidFrame(Width, Height, Bg, delayMs: 100))
            .ToList();

        var palette = GifEncoder.BuildPalette(frames, 2);

        var merged = GifDecoder.Decode(GifEncoder.Encode(frames, palette, options: new GifEncodeOptions
        {
            FrameDiff = true,
            MergeIdenticalFrames = true,
            LossyThreshold = 0,
        }));

        var unmerged = GifDecoder.Decode(GifEncoder.Encode(frames, palette, options: new GifEncodeOptions
        {
            FrameDiff = true,
            MergeIdenticalFrames = false,
            LossyThreshold = 0,
        }));

        Assert.Equal(10, unmerged.Frames.Count);
        Assert.True(merged.Frames.Count < 10, $"合并后还有 {merged.Frames.Count} 帧");

        // 总时长不变（都是 10 × 100ms = 1000ms）
        Assert.Equal(1000, unmerged.TotalDelayMs);
        Assert.Equal(1000, merged.TotalDelayMs);
    }

    // ---------------- ③ 解回来像素正确 ----------------

    [Fact]
    public void FrameDiff_DecodesBackToTheOriginalPixels()
    {
        var frames = BuildMovingSquareFrames(count: 30);
        var palette = GifEncoder.BuildPalette(GifEncoder.SampleFrames(frames, 12), 16);

        var decoded = GifDecoder.Decode(GifEncoder.Encode(frames, palette, options: new GifEncodeOptions
        {
            FrameDiff = true,
            MergeIdenticalFrames = true,
            LossyThreshold = 8,
        }));

        Assert.Equal(Width, decoded.Width);
        Assert.Equal(Height, decoded.Height);

        var expected = frames
            .Select(f => palette.Quantize(f.Bgra, Width, Height))
            .ToList();

        var shown = decoded.ExpandToTimeline(expected.Count, totalDelayMs: 30 * 100);
        Assert.Equal(expected.Count, shown.Count);

        var problems = new List<string>();

        for (var i = 0; i < expected.Count; i++)
        {
            var (x, y) = FirstDifference(expected[i], shown[i]);
            if (x < 0)
            {
                continue;
            }

            var index = (y * Width) + x;
            problems.Add($"第 {i} 帧 ({x},{y}) 解出来是索引 {shown[i][index]}，应为 {expected[i][index]}");
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void LossyThreshold_CollapsesInvisibleChanges()
    {
        // 两帧只差 8/255（刚好在阈值上）：严格编码要换色（2 帧），lossy 之后沿用上一帧索引（合成 1 帧）
        var first = SolidFrame(64, 64, 0x404040, 50);
        var second = SolidFrame(64, 64, 0x484848, 50);
        var palette = GifEncoder.BuildPalette(new[] { first, second }, 4);

        var lossy = GifDecoder.Decode(GifEncoder.Encode(new[] { first, second }, palette, options: new GifEncodeOptions
        {
            FrameDiff = true,
            MergeIdenticalFrames = true,
            LossyThreshold = 8,
        }));

        var strict = GifDecoder.Decode(GifEncoder.Encode(new[] { first, second }, palette, options: new GifEncodeOptions
        {
            FrameDiff = true,
            MergeIdenticalFrames = true,
            LossyThreshold = 0,
        }));

        Assert.Equal(2, strict.Frames.Count);
        Assert.Single(lossy.Frames);
        Assert.Equal(strict.TotalDelayMs, lossy.TotalDelayMs);
    }

    // ---------------- ④ Lanczos3 缩放 ----------------

    [Fact]
    public void Resize_KeepsHardEdgesHard()
    {
        // 左右半屏纯色，缩一半：边缘必须还是硬边（不能糊出一条灰过渡带）
        var src = new byte[64 * 64 * 4];

        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                WritePixel(src, (y * 64) + x, x < 32 ? 0x000000 : 0xFFFFFF);
            }
        }

        var resized = GifResampler.Resize(src, 64, 64, 32, 32);
        Assert.Equal(32 * 32 * 4, resized.Length);

        for (var x = 0; x < 32; x++)
        {
            var actual = ReadPixel(resized, (16 * 32) + x);
            var expected = x < 16 ? 0x000000 : 0xFFFFFF;

            // 允许 ±10 的锐化过冲，但绝不能是中间灰
            for (var channel = 0; channel < 3; channel++)
            {
                Assert.InRange(
                    ReadChannel(actual, channel),
                    ReadChannel(expected, channel) - 10,
                    ReadChannel(expected, channel) + 10);
            }
        }
    }

    [Fact]
    public void Resize_IsNoOpOnSameSize()
    {
        var src = new byte[8 * 8 * 4];
        for (var i = 0; i < 64; i++)
        {
            WritePixel(src, i, 0x336699);
        }

        Assert.Equal(src, GifResampler.Resize(src, 8, 8, 8, 8, sharpen: 0));
    }

    // ---------------- 辅助 ----------------

    private static List<GifFrame> BuildMovingSquareFrames(int count)
    {
        var frames = new List<GifFrame>(count);

        for (var i = 0; i < count; i++)
        {
            var bgra = new byte[Width * Height * 4];

            for (var p = 0; p < Width * Height; p++)
            {
                WritePixel(bgra, p, Bg);
            }

            // 方块从 (8,8) 起步，每帧斜着挪 4px
            var left = 8 + (i * 4 % 150);
            var top = 8 + (i * 4 % 150);

            for (var y = top; y < top + 20; y++)
            {
                for (var x = left; x < left + 20; x++)
                {
                    WritePixel(bgra, (y * Width) + x, Fg);
                }
            }

            frames.Add(new GifFrame(bgra, Width, Height, 100));
        }

        return frames;
    }

    private static GifFrame SolidFrame(int width, int height, int argb, int delayMs)
    {
        var bgra = new byte[width * height * 4];

        for (var i = 0; i < width * height; i++)
        {
            WritePixel(bgra, i, argb);
        }

        return new GifFrame(bgra, width, height, delayMs);
    }

    private static void WritePixel(byte[] bgra, int index, int argb)
    {
        var offset = index * 4;
        bgra[offset] = (byte)(argb & 0xFF);             // B
        bgra[offset + 1] = (byte)((argb >> 8) & 0xFF);  // G
        bgra[offset + 2] = (byte)((argb >> 16) & 0xFF); // R
        bgra[offset + 3] = 0xFF;
    }

    private static int ReadPixel(byte[] bgra, int index)
    {
        var offset = index * 4;
        return (bgra[offset + 2] << 16) | (bgra[offset + 1] << 8) | bgra[offset];
    }

    private static int ReadChannel(int argb, int channel) => channel switch
    {
        0 => (argb >> 16) & 0xFF,
        1 => (argb >> 8) & 0xFF,
        _ => argb & 0xFF,
    };

    private static (int X, int Y) FirstDifference(byte[] expected, byte[] actual)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                return (i % Width, i / Width);
            }
        }

        return (-1, -1);
    }
}

/// <summary>
/// 测试用的最小 GIF 解码器：只认本项目编码器写出来的那点东西
/// （全局色表 / 无局部色表 / 无透明 / disposal=1 / 无隔行），够验证「解回来是对的」。
/// </summary>
internal sealed class GifDecoder
{
    private GifDecoder(int width, int height, List<byte[]> frames, List<int> delays)
    {
        Width = width;
        Height = height;
        Frames = frames;
        Delays = delays;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>合成后的整幅画面（每帧都是 width × height 的调色板索引）。</summary>
    public List<byte[]> Frames { get; }

    /// <summary>每帧延时（毫秒）。</summary>
    public List<int> Delays { get; }

    public int TotalDelayMs => Delays.Sum();

    public static GifDecoder Decode(byte[] gif)
    {
        if (gif.Length < 13 || gif[0] != (byte)'G' || gif[1] != (byte)'I' || gif[2] != (byte)'F')
        {
            throw new InvalidOperationException("不是 GIF 文件");
        }

        var width = gif[6] | (gif[7] << 8);
        var height = gif[8] | (gif[9] << 8);
        var packed = gif[10];
        var position = 13;

        if ((packed & 0x80) != 0)
        {
            position += (1 << ((packed & 0x07) + 1)) * 3;
        }

        var frames = new List<byte[]>();
        var delays = new List<int>();
        var canvas = new byte[width * height];
        var pendingDelay = -1;

        while (position < gif.Length)
        {
            var block = gif[position++];

            if (block == 0x3B)
            {
                break;
            }

            if (block == 0x21)
            {
                var label = gif[position++];

                if (label == 0xF9)
                {
                    var size = gif[position++];
                    pendingDelay = (gif[position + 1] | (gif[position + 2] << 8)) * 10;
                    position += size;
                    position++; // 块结束符
                }
                else
                {
                    position = SkipSubBlocks(gif, position);
                }

                continue;
            }

            if (block != 0x2C)
            {
                throw new InvalidOperationException($"未知的块 0x{block:X2}");
            }

            var left = gif[position] | (gif[position + 1] << 8);
            var top = gif[position + 2] | (gif[position + 3] << 8);
            var frameWidth = gif[position + 4] | (gif[position + 5] << 8);
            var frameHeight = gif[position + 6] | (gif[position + 7] << 8);
            var flags = gif[position + 8];
            position += 9;

            if ((flags & 0x80) != 0)
            {
                throw new InvalidOperationException("测试解码器不支持局部色表");
            }

            if ((flags & 0x40) != 0)
            {
                throw new InvalidOperationException("测试解码器不支持隔行");
            }

            if (left + frameWidth > width || top + frameHeight > height)
            {
                throw new InvalidOperationException("图像块超出画面");
            }

            var minCodeSize = gif[position++];
            var data = ReadSubBlocks(gif, ref position);
            var indices = LzwDecode(data, minCodeSize, frameWidth * frameHeight);

            // disposal=1（不清屏）：只把这一块刷到画布上，其余沿用上一帧
            for (var y = 0; y < frameHeight; y++)
            {
                Buffer.BlockCopy(
                    indices,
                    y * frameWidth,
                    canvas,
                    ((top + y) * width) + left,
                    frameWidth);
            }

            frames.Add((byte[])canvas.Clone());
            delays.Add(Math.Max(10, pendingDelay < 0 ? 0 : pendingDelay));
            pendingDelay = -1;
        }

        return new GifDecoder(width, height, frames, delays);
    }

    /// <summary>
    /// 按时间轴把播放过程展开成「和原始帧一一对应」的画面列表：
    /// 合并过的帧会把时长摊到多个时间片上，这一步还原出每一帧该显示什么。
    /// </summary>
    public List<byte[]> ExpandToTimeline(int expectedFrames, int totalDelayMs)
    {
        var result = new List<byte[]>(expectedFrames);
        var slice = (double)totalDelayMs / Math.Max(1, expectedFrames);

        var index = 0;
        var elapsed = 0d;

        for (var i = 0; i < expectedFrames; i++)
        {
            var time = i * slice;

            while (index + 1 < Frames.Count && elapsed + Delays[index] <= time + 0.001)
            {
                elapsed += Delays[index];
                index++;
            }

            result.Add(Frames[index]);
        }

        return result;
    }

    private static int SkipSubBlocks(byte[] gif, int position)
    {
        while (true)
        {
            var size = gif[position++];
            if (size == 0)
            {
                return position;
            }

            position += size;
        }
    }

    private static byte[] ReadSubBlocks(byte[] gif, ref int position)
    {
        var data = new List<byte>();

        while (true)
        {
            var size = gif[position++];
            if (size == 0)
            {
                break;
            }

            for (var i = 0; i < size; i++)
            {
                data.Add(gif[position + i]);
            }

            position += size;
        }

        return data.ToArray();
    }

    /// <summary>标准 GIF LZW 解码（码长增长 / ClearCode / KwKwK 都要和编码器对上）。</summary>
    private static byte[] LzwDecode(byte[] data, int minCodeSize, int pixelCount)
    {
        var clearCode = 1 << minCodeSize;
        var endCode = clearCode + 1;

        var prefix = new int[4096];
        var suffix = new byte[4096];
        var stack = new byte[4096];

        var output = new byte[pixelCount];
        var written = 0;

        var codeSize = minCodeSize + 1;
        var nextCode = endCode + 1;
        var previous = -1;
        var bitPosition = 0;

        void ResetTable()
        {
            codeSize = minCodeSize + 1;
            nextCode = endCode + 1;
            previous = -1;
        }

        ResetTable();

        while (written < pixelCount && bitPosition + codeSize <= data.Length * 8)
        {
            var code = 0;
            for (var i = 0; i < codeSize; i++)
            {
                var bit = (data[(bitPosition + i) >> 3] >> ((bitPosition + i) & 7)) & 1;
                code |= bit << i;
            }

            bitPosition += codeSize;

            if (code == clearCode)
            {
                ResetTable();
                continue;
            }

            if (code == endCode)
            {
                break;
            }

            var kwkwk = previous >= 0 && code >= nextCode;
            var entry = kwkwk ? previous : code;
            var stackTop = 0;
            var current = entry;

            while (current >= clearCode)
            {
                stack[stackTop++] = suffix[current];
                current = prefix[current];
            }

            var first = current;
            stack[stackTop++] = (byte)current;

            for (var i = stackTop - 1; i >= 0 && written < pixelCount; i--)
            {
                output[written++] = stack[i];
            }

            if (kwkwk && written < pixelCount)
            {
                output[written++] = (byte)first;
            }

            if (previous >= 0 && nextCode < 4096)
            {
                prefix[nextCode] = previous;
                suffix[nextCode] = (byte)first;
                nextCode++;

                // 解码端的字典比编码端晚一格，所以码长要比编码器早一步增长（标准 GIF 解码的做法）
                if (nextCode == (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }

            previous = code;
        }

        return output;
    }
}
