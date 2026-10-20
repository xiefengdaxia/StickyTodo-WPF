using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// 生成应用图标（多尺寸 ICO）。
/// 设计：便签纸 + 右上角折角 + 一个勾，和程序里的「经典黄」皮肤一致。
/// 用 WPF 矢量绘制后按各尺寸栅格化，因此 16px 到 256px 都清晰。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var output = args.Length > 0
            ? args[0]
            : Path.Combine("src", "StickyTodo.App", "Assets", "stickytodo.ico");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = sizes.Select(s => Rasterize(RenderNote(s, s), s, s)).ToList();

        using (var fs = File.Create(output))
        using (var bw = new BinaryWriter(fs))
        {
            // ICONDIR
            bw.Write((ushort)0);          // reserved
            bw.Write((ushort)1);          // type = icon
            bw.Write((ushort)sizes.Length);

            var offset = 6 + (16 * sizes.Length);

            for (var i = 0; i < sizes.Length; i++)
            {
                var s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));   // width
                bw.Write((byte)(s >= 256 ? 0 : s));   // height
                bw.Write((byte)0);                     // palette
                bw.Write((byte)0);                     // reserved
                bw.Write((ushort)1);                   // planes
                bw.Write((ushort)32);                  // bpp
                bw.Write(frames[i].Length);
                bw.Write(offset);
                offset += frames[i].Length;
            }

            foreach (var frame in frames)
            {
                bw.Write(frame);
            }
        }

        Console.WriteLine($"written: {output} ({sizes.Length} sizes: {string.Join(", ", sizes)})");

        // 同时输出一张预览图，方便肉眼验收各尺寸效果
        var preview = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "icon-preview.png");
        RenderPreview(output, preview);
        Console.WriteLine($"preview: {preview}");
    }

    /// <summary>把所有尺寸并排画在一张图里，方便检查小尺寸是否还认得出。</summary>
    private static void RenderPreview(string icoPath, string previewPath)
    {
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var width = sizes.Sum(s => s + 16) + 16;
        const int height = 300;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x16, 0x2B, 0x3C)), null, new Rect(0, 0, width, height));

            var decoder = BitmapDecoder.Create(new Uri(icoPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            double x = 16;
            foreach (var s in sizes)
            {
                var frame = decoder.Frames.FirstOrDefault(f => f.PixelWidth == s) ?? decoder.Frames[0];
                dc.DrawImage(frame, new Rect(x, 20, s, s));
                x += s + 16;
            }
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var fs = File.Create(previewPath);
        encoder.Save(fs);
    }

    /// <summary>按目标像素尺寸画一张便签（矢量绘制，坐标都按 size 等比换算）。</summary>
    private static DrawingVisual RenderNote(double size, double actual)
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();

        var paperTop = Color.FromRgb(0xFF, 0xF4, 0xB8);
        var paperBottom = Color.FromRgb(0xFB, 0xE1, 0x8A);
        var fold = Color.FromRgb(0xE9, 0xC4, 0x60);
        var ink = Color.FromRgb(0x3B, 0x38, 0x2A);
        var accent = Color.FromRgb(0xB2, 0x7F, 0x0A);

        // 安全边距：小尺寸下留白少一点，图形才不至于太小
        var pad = size * (size <= 24 ? 0.03 : 0.07);
        var rect = new Rect(pad, pad, size - (pad * 2), size - (pad * 2));
        var radius = size * 0.17;
        var foldSize = rect.Width * 0.36;

        // 1) 阴影
        var shadow = new Rect(rect.X, rect.Y + (size * 0.035), rect.Width, rect.Height);
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(size <= 32 ? (byte)0x3A : (byte)0x2C, 0, 0, 0)),
            null, shadow, radius, radius);

        // 2) 纸面（竖向渐变）
        var paper = new LinearGradientBrush(paperTop, paperBottom, 90);
        dc.DrawRoundedRectangle(paper, null, rect, radius, radius);

        // 3) 右上角折角
        var foldGeometry = new StreamGeometry();
        using (var ctx = foldGeometry.Open())
        {
            ctx.BeginFigure(new Point(rect.Right - foldSize, rect.Top), true, true);
            ctx.LineTo(new Point(rect.Right, rect.Top + foldSize), true, false);
            ctx.LineTo(new Point(rect.Right - foldSize, rect.Top + foldSize), true, false);
        }

        foldGeometry.Freeze();
        dc.DrawGeometry(new SolidColorBrush(fold), null, foldGeometry);

        // 4) 勾：小尺寸用深墨色保证对比，大尺寸用主色
        var strokeWidth = Math.Max(1.3, size * 0.085);
        var pen = new Pen(new SolidColorBrush(size <= 20 ? ink : accent), strokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var cx = rect.X + (rect.Width * 0.27);
        var cy = rect.Y + (rect.Height * 0.53);
        var w = rect.Width;
        var h = rect.Height;

        var check = new StreamGeometry();
        using (var ctx = check.Open())
        {
            ctx.BeginFigure(new Point(cx, cy + (h * 0.05)), false, false);
            ctx.LineTo(new Point(cx + (w * 0.17), cy + (h * 0.22)), true, true);
            ctx.LineTo(new Point(cx + (w * 0.47), cy - (h * 0.21)), true, true);
        }

        check.Freeze();
        dc.DrawGeometry(null, pen, check);

        return visual;
    }

    private static byte[] Rasterize(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}