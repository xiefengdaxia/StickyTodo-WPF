using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Serilog;
using StickyTodo.App.Services;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Views;

/// <summary>
/// 多屏时的选屏浮层：每块屏一个按钮（序号 + 分辨率 + 主屏标记），
/// 按钮本身按各屏的相对位置摆开，一眼就能看出哪块在左、哪块在上。
///
/// 录全屏时如果直接抓「整个虚拟桌面」，多屏拼出来的 GIF 又长又没用，
/// 所以先在单屏（<see cref="ScreenCaptureService.GetScreens"/>）里选一块。
/// 点按钮开始录，Esc 取消。
/// </summary>
public partial class GifScreenPickerWindow : Window
{
    /// <summary>按钮的最小尺寸：1920×1080 的屏缩到示意图里也没多少像素，太小不好点。</summary>
    private const double MinButtonWidth = 86;

    private const double MinButtonHeight = 62;

    /// <summary>示意图的最大高度（宽度由 XAML 里画布宽度决定）。</summary>
    private const double MaxDiagramHeight = 260;

    private readonly IReadOnlyList<ScreenCaptureService.ScreenTarget> _screens;

    /// <summary>用户选中的那块屏；取消 / 直接关掉时为 null。</summary>
    public ScreenCaptureService.ScreenTarget? Selected { get; private set; }

    public GifScreenPickerWindow(IReadOnlyList<ScreenCaptureService.ScreenTarget> screens)
    {
        InitializeComponent();

        _screens = screens;

        TitleText.Text = Loc.T("gif.pickScreen");
        HintText.Text = Loc.T("gif.pickScreenHint");

        Loaded += (_, _) => BuildDiagram();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>按各屏在虚拟桌面里的相对位置铺按钮（等比缩放，居中）。</summary>
    private void BuildDiagram()
    {
        Diagram.Children.Clear();

        if (_screens.Count == 0)
        {
            return;
        }

        // 各屏外接矩形 = 虚拟桌面；用它的宽高做缩放基准
        var left = _screens.Min(s => s.Bounds.Left);
        var top = _screens.Min(s => s.Bounds.Top);
        var right = _screens.Max(s => s.Bounds.Right);
        var bottom = _screens.Max(s => s.Bounds.Bottom);
        var unionWidth = Math.Max(1, right - left);
        var unionHeight = Math.Max(1, bottom - top);

        var canvasWidth = Diagram.Width;
        var scale = Math.Min(canvasWidth / unionWidth, MaxDiagramHeight / unionHeight);

        var diagramWidth = unionWidth * scale;
        var diagramHeight = unionHeight * scale;

        Diagram.Height = Math.Max(MinButtonHeight, Math.Ceiling(diagramHeight));

        // 小屏叠大屏时外接矩形会偏宽，等比缩放后还会剩一条边，居中一下更整齐
        var offsetX = Math.Max(0, (canvasWidth - diagramWidth) / 2);
        var offsetY = 0d;

        foreach (var screen in _screens)
        {
            var width = Math.Max(MinButtonWidth, screen.Bounds.Width * scale);
            var height = Math.Max(MinButtonHeight, screen.Bounds.Height * scale);

            // 缩放后的中心点不变，被放大到最小尺寸的按钮仍然对着那块屏的位置
            var centerX = offsetX + ((screen.Bounds.Left + (screen.Bounds.Width / 2d)) - left) * scale;
            var centerY = offsetY + ((screen.Bounds.Top + (screen.Bounds.Height / 2d)) - top) * scale;

            var button = BuildScreenButton(screen, width, height);
            Canvas.SetLeft(button, Math.Round(centerX - (width / 2)));
            Canvas.SetTop(button, Math.Round(centerY - (height / 2)));
            Diagram.Children.Add(button);
        }

        Log.Debug("[gif] 选屏浮层：{Count} 块屏（{Width}x{Height} 虚拟桌面 → 缩放 {Scale:P0}）",
            _screens.Count, unionWidth, unionHeight, scale);
    }

    private Button BuildScreenButton(ScreenCaptureService.ScreenTarget screen, double width, double height)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        stack.Children.Add(new TextBlock
        {
            Text = screen.Index.ToString(),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        stack.Children.Add(new TextBlock
        {
            Text = screen.Resolution,
            FontSize = 11,
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        if (screen.IsPrimary)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Loc.T("gif.primaryScreen"),
                FontSize = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3F, 0xA9, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        var button = new Button
        {
            Width = width,
            Height = height,
            Content = stack,
            Style = (Style)FindResource("ScreenButton"),
            ToolTip = $"{screen.Index} · {screen.Resolution} · {screen.DeviceName}",
        };

        // DialogResult 一设窗口就会自己关，不要再 Close 一次
        button.Click += (_, _) =>
        {
            Selected = screen;
            DialogResult = true;
        };

        return button;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        // Esc = 这次不录了，ShowDialog 返回 false
        DialogResult = false;
        e.Handled = true;
    }
}
