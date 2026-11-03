using System.Drawing;
using WinForms = System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>
/// 给托盘右键菜单（WinForms ContextMenuStrip）套上当前皮肤配色。
///
/// 托盘菜单是 WinForms 控件，不认 WPF 的 DynamicResource，所以这里把 NoteSkin 的
/// 颜色换算成 GDI+ 颜色 + 自定义 ProfessionalColorTable，让菜单底色、选中高亮、
/// 分隔线、文字颜色都跟着皮肤走（含 3 套暗色皮肤）。
/// </summary>
internal static class TrayMenuSkin
{
    public static void Apply(WinForms.ContextMenuStrip menu, NoteSkin skin)
    {
        var background = ToDrawing(Blend(skin.BackgroundBottom, skin.BackgroundTop, 0.35));
        var text = ToDrawing(skin.Ink);
        var soft = ToDrawing(skin.InkSoft);
        var accent = ToDrawing(skin.Accent);
        var line = ToDrawing(Blend(skin.InkSoft, skin.BackgroundBottom, 0.75));
        var highlight = ToDrawing(Blend(skin.Accent, skin.BackgroundBottom, 0.78));

        menu.BackColor = background;
        menu.ForeColor = text;
        menu.Font = new Font("Microsoft YaHei UI", 9f);
        menu.Padding = new WinForms.Padding(2, 4, 2, 4);
        menu.Renderer = new SkinRenderer(
            new SkinColorTable(background, text, highlight, line, accent));

        ApplyItemColors(menu.Items, text, soft);

        // 圆角：ContextMenuStrip 没有 CornerRadius，用 Region 削角（打开时按实际尺寸重算）
        menu.Opened += (_, _) => ApplyRoundedRegion(menu, 8);
    }

    /// <summary>把菜单窗口裁成圆角矩形（尺寸变了要重算，所以每次打开都做）。</summary>
    private static void ApplyRoundedRegion(WinForms.ContextMenuStrip menu, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var w = menu.Width;
        var h = menu.Height;
        if (w < radius * 2 || h < radius * 2)
        {
            return;
        }

        var d = radius * 2;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(w - d, 0, d, d, 270, 90);
        path.AddArc(w - d, h - d, d, d, 0, 90);
        path.AddArc(0, h - d, d, d, 90, 90);
        path.CloseFigure();

        var old = menu.Region;
        menu.Region = new System.Drawing.Region(path);
        old?.Dispose();
    }

    private static void ApplyItemColors(WinForms.ToolStripItemCollection items, Color text, Color soft)
    {
        foreach (WinForms.ToolStripItem item in items)
        {
            // 分隔线没有文字；子菜单标题统一用主文字色
            if (item is WinForms.ToolStripMenuItem menuItem)
            {
                menuItem.ForeColor = menuItem.Enabled ? text : soft;
                menuItem.Padding = new WinForms.Padding(4, 3, 4, 3);
                if (menuItem.HasDropDownItems)
                {
                    ApplyItemColors(menuItem.DropDownItems, text, soft);
                }
            }
        }
    }

    /// <summary>把两种颜色按比例混合（<paramref name="ratio"/> 越大越靠后一种）。</summary>
    private static System.Windows.Media.Color Blend(
        System.Windows.Media.Color from,
        System.Windows.Media.Color to,
        double ratio)
    {
        var r = ratio <= 0 ? 0 : ratio >= 1 ? 1 : ratio;
        return System.Windows.Media.Color.FromRgb(
            (byte)(from.R + ((to.R - from.R) * r)),
            (byte)(from.G + ((to.G - from.G) * r)),
            (byte)(from.B + ((to.B - from.B) * r)));
    }

    private static Color ToDrawing(System.Windows.Media.Color color) =>
        Color.FromArgb(color.R, color.G, color.B);

    private sealed class SkinColorTable(
        Color background,
        Color text,
        Color highlight,
        Color line,
        Color accent) : WinForms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => background;
        public override Color ImageMarginGradientBegin => background;
        public override Color ImageMarginGradientMiddle => background;
        public override Color ImageMarginGradientEnd => background;
        public override Color MenuBorder => line;
        public override Color MenuItemBorder => highlight;
        public override Color MenuItemSelected => highlight;
        public override Color MenuItemSelectedGradientBegin => highlight;
        public override Color MenuItemSelectedGradientEnd => highlight;
        public override Color MenuItemPressedGradientBegin => highlight;
        public override Color MenuItemPressedGradientEnd => highlight;
        public override Color SeparatorDark => line;
        public override Color SeparatorLight => line;
        public override Color CheckBackground => highlight;
        public override Color CheckSelectedBackground => highlight;
        public override Color CheckPressedBackground => highlight;
        public override Color ButtonSelectedHighlight => highlight;
        public override Color ToolStripBorder => line;
        public override Color StatusStripGradientBegin => background;
        public override Color StatusStripGradientEnd => background;
    }

    /// <summary>默认渲染器在浅色皮肤上会把选中行画成系统蓝，这里只换配色不改排版。</summary>
    private sealed class SkinRenderer(WinForms.ProfessionalColorTable table)
        : WinForms.ToolStripProfessionalRenderer(table);
}