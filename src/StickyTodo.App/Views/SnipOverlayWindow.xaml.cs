using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace StickyTodo.App.Views;

/// <summary>
/// 覆盖层必须**像素级**贴住整个虚拟屏，否则截图和真实桌面会错开几个像素
/// （任务栏、屏幕右缘被切）。两件事一起做才能对齐：
/// 1) 拦 WM_GETMINMAXINFO —— Windows 默认把普通窗口的「最大跟踪尺寸」限制在单屏，
///    多屏（尤其是横向拼接）时窗口会被悄悄缩小，右侧就被截掉；
/// 2) 用 SetWindowPos 按像素矩形摆位，再按窗口自己的 DPI 折算成 WPF 的 DIP 尺寸。
/// </summary>

/// <summary>
/// 截图覆盖层：铺满整个虚拟桌面，拖拽选区，然后在选区内画画 / 画线 / 框选 / 加文字。
///
/// 截图是在窗口出现**之前**抓好的，所以覆盖层自己不会被抓进去；
/// 选区只是从那张图上裁一块出来提亮。
/// </summary>
public partial class SnipOverlayWindow : Window
{
    private enum Tool
    {
        Select,
        Pen,
        Line,
        Rect,
        Text,
    }

    private readonly BitmapSource _screenshot;
    private readonly List<UIElement> _strokes = new();

    private Tool _tool = Tool.Select;
    private Color _color = Colors.Red;
    private double _thickness = 3;

    private Rect _selection;
    private bool _hasSelection;

    // 双击＝直接截图：记住进入「重新框选」之前的选区，第二下点下去直接出图
    private Rect _selectionBeforeReframe;
    private bool _hadSelectionBeforeReframe;

    // 选区拖拽
    private bool _dragging;
    private Point _dragOrigin;

    // 当前笔画
    private Shape? _currentShape;
    private Polyline? _currentPolyline;
    private bool _drawing;
    private Point _drawOrigin;
    private TextBox? _editingText;

    /// <summary>回归测试用：不铺满虚拟桌面，按指定尺寸显示。</summary>
    private bool _exactSizeForTest;

    /// <summary>点「贴图」后的成品图；取消时为 null。</summary>
    public BitmapSource? Result { get; private set; }

    /// <summary>
    /// 点「录 GIF」时的选区（**屏幕物理像素**）；没点过就是 null。
    /// 调用方看到它非 null 就跳过贴图，直接开录。
    /// </summary>
    public System.Drawing.Rectangle? GifRegion { get; private set; }

    /// <summary>
    /// 点了右下角「全屏」：这次不框选，直接按整屏录。
    /// 调用方看到它非 null 就不用管 <see cref="GifRegion"/>，走和托盘「全屏」一样的入口。
    /// </summary>
    public bool GifFullScreenRequested { get; private set; }

    /// <summary>这次遮罩是为「录 GIF」开的（用来露出全屏入口），默认是普通截图。</summary>
    private readonly bool _gifMode;

    public SnipOverlayWindow(BitmapSource screenshot)
        : this(screenshot, gifMode: false)
    {
    }

    /// <param name="gifMode">
    /// true = 从「录 GIF」进来：右下角多一个「全屏」按钮，提示文案也会说明可以不框选。
    /// </param>
    public SnipOverlayWindow(BitmapSource screenshot, bool gifMode)
    {
        InitializeComponent();

        _screenshot = screenshot;
        _gifMode = gifMode;
        Backdrop.Source = screenshot;
        Bright.Source = screenshot;

        // 工具条上的「录 GIF」用文案表里的 key，切语言时跟着变
        RecordGifButton.Content = StickyTodo.Core.Localization.Loc.T("gif.record");

        if (gifMode)
        {
            var fullScreenTip = StickyTodo.Core.Localization.Loc.T("gif.fullscreenTip");

            FullScreenButton.Content = "▣ " + StickyTodo.Core.Localization.Loc.T("gif.fullscreen");
            FullScreenButton.ToolTip = fullScreenTip;
            FullScreenHint.Text = fullScreenTip;

            // 框选仍是默认动作，但提示里写清楚右下角可以直接录全屏
            Hint.Text = StickyTodo.Core.Localization.Loc.T("gif.regionHint");

            FullScreenPanel.Visibility = Visibility.Visible;
        }

        // 初始：整屏压暗，没有"亮洞"
        ShowFullDim();
    }

    /// <summary>整屏压暗（无选区）。压暗层自己铺满窗口，这里只把「亮洞」藏掉。</summary>
    private void ShowFullDim()
    {
        Bright.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 用一个矩形 Clip 在压暗层上「挖洞」：压暗层是静态的，拖动时只有这张原图的
    /// 裁剪几何在变，不会像以前那样每帧重排四块矩形、整窗重绘。
    /// </summary>
    private void ShowBrightArea(Rect selection)
    {
        var rect = new Rect(
            Math.Max(0, selection.X),
            Math.Max(0, selection.Y),
            Math.Max(0, selection.Width),
            Math.Max(0, selection.Height));

        Bright.Clip = rect.Width > 0 && rect.Height > 0 ? new RectangleGeometry(rect) : null;
        Bright.Visibility = rect.Width > 0 && rect.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 铺满整个虚拟桌面（多显示器也是一整块）
        if (!_exactSizeForTest)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
                ApplyVirtualScreenGeometry(hwnd);
            }
        }

        Loaded += (_, _) =>
        {
            Activate();
            Focus();

            // Show 之后 DPI/位置才定型，这里再压一遍像素矩形，避免被 WPF 二次缩放
            if (!_exactSizeForTest)
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ApplyVirtualScreenGeometry(hwnd);
                }
            }
        };

        PreviewKeyDown += OnPreviewKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;

        // WPF 在照片加载/布局收尾时可能再改一次窗口尺寸，漂了就压回去
        SizeChanged += (_, _) =>
        {
            if (_exactSizeForTest || !_geometryApplied || _resyncCount >= 3)
            {
                return;
            }

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var bounds = Services.ScreenCaptureService.GetVirtualScreenBounds();
            var scale = GetDpiForWindow(hwnd) / 96.0;
            if (scale <= 0)
            {
                scale = 1;
            }

            if (Math.Abs(ActualWidth * scale - bounds.Width) < 0.5 &&
                Math.Abs(ActualHeight * scale - bounds.Height) < 0.5)
            {
                return;
            }

            _resyncCount++;
            Serilog.Log.Information("[snip] 窗口尺寸漂移 {W}x{H}（DIP，第 {N} 次），重新对齐虚拟屏",
                ActualWidth, ActualHeight, _resyncCount);
            ApplyVirtualScreenGeometry(hwnd);
        };

        // 覆盖层可能跨两块 DPI 不同的屏：DPI 一变就重算像素矩形
        DpiChanged += (_, _) =>
        {
            if (_exactSizeForTest)
            {
                return;
            }

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ApplyVirtualScreenGeometry(hwnd);
            }
        };
    }

    // ---------------- 铺满虚拟屏（像素级） ----------------

    private const int WmGetMinMaxInfo = 0x0024;
    private const int SwpNoActivate = 0x0010;

    private static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>
    /// Windows 会把普通窗口的最大尺寸限制在「单屏 + 边框」；多屏横向拼接时窗口被缩小，
    /// 截图右侧就缺一条。这里把最大跟踪尺寸直接改成整个虚拟屏。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var bounds = Services.ScreenCaptureService.GetVirtualScreenBounds();
        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition = new NativePoint(bounds.Left, bounds.Top);
        info.MaxSize = new NativePoint(bounds.Width, bounds.Height);
        info.MaxTrackSize = new NativePoint(bounds.Width, bounds.Height);
        System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>像素矩形已经压准过一次（之后才允许尺寸漂移纠正）。</summary>
    private bool _geometryApplied;

    /// <summary>尺寸漂移的纠正次数上限，防止和 WPF 的布局互相拉扯。</summary>
    private int _resyncCount;

    /// <summary>
    /// 按虚拟屏的像素矩形摆窗口。WPF 的 Width/Height 是 DIP，会再用**窗口当时的 DPI**
    /// 换算回像素；如果拿创建时的 DPI 算，窗口跨到另一块缩放不同的屏上就会被二次缩放
    /// （实测 2592px 变成 2160px）。所以：先以当前 DPI 设 DIP，再用 SetWindowPos 定像素，
    /// 最后回读一次 GetWindowRect，不一致就再压一遍。
    /// </summary>
    private void ApplyVirtualScreenGeometry(IntPtr hwnd)
    {
        var bounds = Services.ScreenCaptureService.GetVirtualScreenBounds();
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }

        Left = bounds.Left / scale;
        Top = bounds.Top / scale;
        Width = bounds.Width / scale;
        Height = bounds.Height / scale;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            SetWindowPos(hwnd, HwndTopmost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpNoActivate);

            if (!GetWindowRect(hwnd, out var rect))
            {
                return;
            }

            var actualW = rect.Right - rect.Left;
            var actualH = rect.Bottom - rect.Top;
            if (rect.Left == bounds.Left && rect.Top == bounds.Top &&
                actualW == bounds.Width && actualH == bounds.Height)
            {
                Serilog.Log.Information(
                    "[snip] 覆盖层对齐虚拟屏: 期望 {W}x{H} @ {X},{Y} · 实际 {AW}x{AH} @ {AX},{AY} · dpi={Dpi} · 尝试 {Attempt}",
                    bounds.Width, bounds.Height, bounds.Left, bounds.Top,
                    actualW, actualH, rect.Left, rect.Top, GetDpiForWindow(hwnd), attempt + 1);
                _geometryApplied = true;
                return;
            }

            Serilog.Log.Warning(
                "[snip] 覆盖层尺寸不对: 实际 {AW}x{AH} @ {AX},{AY}，期望 {W}x{H} @ {X},{Y} · dpi={Dpi}",
                actualW, actualH, rect.Left, rect.Top,
                bounds.Width, bounds.Height, bounds.Left, bounds.Top, GetDpiForWindow(hwnd));
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // ---------------- 选区 ----------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 工具条 / 输入框上的点击留给它们自己
        if (e.OriginalSource is DependencyObject source && IsInsideToolbar(source))
        {
            return;
        }

        var point = e.GetPosition(Root);

        if (CommitText())
        {
            return;
        }

        if (_hasSelection && _tool != Tool.Select)
        {
            StartDrawing(point, e);
            return;
        }

        // 双击的第二下：恢复进入框选前的选区，直接截图收工
        if (e.ClickCount >= 2 && _hadSelectionBeforeReframe && _tool == Tool.Select)
        {
            _selection = _selectionBeforeReframe;
            _hasSelection = true;
            CopyOnly();
            return;
        }

        // 记住框选前的选区（双击的第二下要用）
        _hadSelectionBeforeReframe = _hasSelection;
        _selectionBeforeReframe = _selection;

        // 重新框选
        _dragging = true;
        _dragOrigin = point;
        _hasSelection = false;
        ClearAnnotations();
        Toolbar.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Collapsed;
        ShowFullDim();
        Hint.Visibility = Visibility.Collapsed;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Root);

        if (_dragging)
        {
            var rect = Normalize(_dragOrigin, point);
            _selection = rect;
            ApplySelectionVisual(rect, showHandles: false);
            return;
        }

        if (_drawing)
        {
            ContinueDrawing(point);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;

            if (_selection.Width < 8 || _selection.Height < 8)
            {
                // 太小的框当作误点：恢复提示
                _selection = Rect.Empty;
                _hasSelection = false;
                _hadSelectionBeforeReframe = false;
                SelectionBorder.Visibility = Visibility.Collapsed;
                ShowFullDim();
                Hint.Visibility = Visibility.Visible;
                return;
            }

            _hasSelection = true;
            _hadSelectionBeforeReframe = false;
            ApplySelectionVisual(_selection, showHandles: true);
            ShowToolbar();
            return;
        }

        if (_drawing)
        {
            FinishDrawing();
        }
    }

    private void ApplySelectionVisual(Rect rect, bool showHandles)
    {
        rect = Clamp(rect);

        ShowBrightArea(rect);

        Canvas.SetLeft(SelectionBorder, rect.X);
        Canvas.SetTop(SelectionBorder, rect.Y);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        SelectionBorder.Visibility = Visibility.Visible;

        Canvas.SetLeft(AnnoLayer, rect.X);
        Canvas.SetTop(AnnoLayer, rect.Y);
        AnnoLayer.Width = rect.Width;
        AnnoLayer.Height = rect.Height;

        var px = $"{(int)Math.Round(rect.Width * ScaleX)} × {(int)Math.Round(rect.Height * ScaleY)}";
        if (!string.Equals(SizeText.Text, px, StringComparison.Ordinal))
        {
            SizeText.Text = px;
        }

        SizeBadge.Visibility = Visibility.Visible;
        Canvas.SetLeft(SizeBadge, rect.X);
        Canvas.SetTop(SizeBadge, Math.Max(0, rect.Y - 26));
    }

    private void ShowToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.UpdateLayout();

        var width = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : 560;
        var height = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : 40;

        var left = Math.Clamp(_selection.X, 0, Math.Max(0, Width - width - 8));
        var below = _selection.Y + _selection.Height + 8;
        var top = below + height <= Height - 8 ? below : Math.Max(8, _selection.Y - height - 8);

        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
    }

    private double ScaleX => _screenshot.PixelWidth > 0 && Root.ActualWidth > 0
        ? _screenshot.PixelWidth / Root.ActualWidth
        : 1;

    private double ScaleY => _screenshot.PixelHeight > 0 && Root.ActualHeight > 0
        ? _screenshot.PixelHeight / Root.ActualHeight
        : 1;

    private Rect Clamp(Rect rect)
    {
        var w = Math.Min(rect.Width, Math.Max(0, Width - rect.X));
        var h = Math.Min(rect.Height, Math.Max(0, Height - rect.Y));
        return new Rect(rect.X, rect.Y, Math.Max(1, w), Math.Max(1, h));
    }

    private static Rect Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private bool IsInsideToolbar(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            // 右下角的全屏入口也算工具条：点它（含边距）不该被当成框选拖拽
            if (ReferenceEquals(node, Toolbar) || ReferenceEquals(node, FullScreenPanel))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------- 工具条 ----------------

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        _tool = tag switch
        {
            "pen" => Tool.Pen,
            "line" => Tool.Line,
            "rect" => Tool.Rect,
            "text" => Tool.Text,
            _ => Tool.Select,
        };

        // 高亮当前工具
        foreach (var item in new[] { PenButton, LineButton, RectButton, TextButton })
        {
            item.Background = ReferenceEquals(item, button)
                ? new SolidColorBrush(Color.FromArgb(0x55, 0x3F, 0xA9, 0xF5))
                : Brushes.Transparent;
        }
    }

    private void Thickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _thickness = Math.Max(1, e.NewValue);

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && ColorConverter.ConvertFromString(hex) is Color color)
        {
            _color = color;
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyOnly();

    private void Pin_Click(object sender, RoutedEventArgs e) => Pin();

    private void RecordGif_Click(object sender, RoutedEventArgs e) => RecordGif();

    private void FullScreen_Click(object sender, RoutedEventArgs e) => RecordGifFullScreen();

    // ---------------- 绘制 ----------------

    private void StartDrawing(Point point, MouseButtonEventArgs e)
    {
        if (!_selection.Contains(point))
        {
            return;
        }

        _drawing = true;
        _drawOrigin = e.GetPosition(AnnoLayer);
        _currentPolyline = null;
        _currentShape = null;

        switch (_tool)
        {
            case Tool.Pen:
                _currentPolyline = new Polyline
                {
                    Stroke = new SolidColorBrush(_color),
                    StrokeThickness = _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _currentPolyline.Points.Add(_drawOrigin);
                AnnoLayer.Children.Add(_currentPolyline);
                _strokes.Add(_currentPolyline);
                break;

            case Tool.Line:
                _currentShape = new Line
                {
                    X1 = _drawOrigin.X, Y1 = _drawOrigin.Y,
                    X2 = _drawOrigin.X, Y2 = _drawOrigin.Y,
                    Stroke = new SolidColorBrush(_color),
                    StrokeThickness = _thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                AnnoLayer.Children.Add(_currentShape);
                _strokes.Add(_currentShape);
                break;

            case Tool.Rect:
                _currentShape = new Rectangle
                {
                    Stroke = new SolidColorBrush(_color),
                    StrokeThickness = _thickness,
                    Fill = Brushes.Transparent,
                };
                Canvas.SetLeft(_currentShape, _drawOrigin.X);
                Canvas.SetTop(_currentShape, _drawOrigin.Y);
                AnnoLayer.Children.Add(_currentShape);
                _strokes.Add(_currentShape);
                break;

            case Tool.Text:
                BeginText(_drawOrigin);
                _drawing = false;
                break;
        }
    }

    private void ContinueDrawing(Point point)
    {
        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);

        if (_currentPolyline is not null)
        {
            _currentPolyline.Points.Add(local);
            return;
        }

        if (_currentShape is Line line)
        {
            line.X2 = local.X;
            line.Y2 = local.Y;
            return;
        }

        if (_currentShape is Rectangle rect)
        {
            var x = Math.Min(_drawOrigin.X, local.X);
            var y = Math.Min(_drawOrigin.Y, local.Y);
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            rect.Width = Math.Abs(local.X - _drawOrigin.X);
            rect.Height = Math.Abs(local.Y - _drawOrigin.Y);
        }
    }

    private void FinishDrawing()
    {
        _drawing = false;
        _currentShape = null;
        _currentPolyline = null;
    }

    private void BeginText(Point local)
    {
        CommitText();

        _editingText = new TextBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            Foreground = new SolidColorBrush(_color),
            BorderThickness = new Thickness(0),
            FontSize = Math.Max(16, _thickness * 5),
            MinWidth = 80,
            Padding = new Thickness(2),
            AcceptsReturn = false,
        };

        Canvas.SetLeft(_editingText, local.X);
        Canvas.SetTop(_editingText, local.Y);
        AnnoLayer.Children.Add(_editingText);
        _editingText.Focus();
    }

    private bool CommitText()
    {
        if (_editingText is null)
        {
            return false;
        }

        var text = _editingText.Text;
        var left = Canvas.GetLeft(_editingText);
        var top = Canvas.GetTop(_editingText);
        var fontSize = _editingText.FontSize;

        AnnoLayer.Children.Remove(_editingText);
        _editingText = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            var block = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(_color),
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
            };

            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, top);
            AnnoLayer.Children.Add(block);
            _strokes.Add(block);
        }

        return true;
    }

    private void Undo()
    {
        if (_strokes.Count == 0)
        {
            return;
        }

        var last = _strokes[^1];
        _strokes.RemoveAt(_strokes.Count - 1);
        AnnoLayer.Children.Remove(last);
    }

    private void ClearAnnotations()
    {
        AnnoLayer.Children.Clear();
        _strokes.Clear();
        _editingText = null;
    }

    // ---------------- 键盘 ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (CommitText())
                {
                    e.Handled = true;
                    return;
                }

                Cancel();
                e.Handled = true;
                break;

            case Key.Enter:
                if (CommitText())
                {
                    e.Handled = true;
                    return;
                }

                Pin();
                e.Handled = true;
                break;

            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                Undo();
                e.Handled = true;
                break;

            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                CopyOnly();
                e.Handled = true;
                break;
        }
    }

    // ---------------- 输出 ----------------

    private void Cancel()
    {
        Result = null;
        DialogResult = false;
    }

    /// <summary>把「截图 + 标注」合成一张图，然后关掉覆盖层。</summary>
    public void Pin()
    {
        if (!_hasSelection || _selection.Width < 1 || _selection.Height < 1)
        {
            return;
        }

        Result = Compose();

        // 贴图的同时也放进剪贴板：可以直接 Ctrl+V 粘到聊天框
        if (Result is not null)
        {
            CopyToClipboardInBackground(Result);
        }

        DialogResult = true;
    }

    /// <summary>工具栏「截图」：只放进剪贴板，不贴到桌面。</summary>
    public void CopyOnly()
    {
        if (!_hasSelection || _selection.Width < 1 || _selection.Height < 1)
        {
            return;
        }

        Result = Compose();
        CopiedOnly = Result is not null;

        if (Result is not null)
        {
            CopyToClipboardInBackground(Result);
        }

        DialogResult = true;
    }

    /// <summary>用户是不是只选择了「截图」（不要贴到桌面）。</summary>
    public bool CopiedOnly { get; private set; }

    /// <summary>
    /// 工具栏「录 GIF」：把当前框选交给录制流程，遮罩立刻关掉（不然工具条会被录进去）。
    /// 不做合成、不进剪贴板 —— 录制由 <see cref="Services.GifRecordingService"/> 接手。
    /// </summary>
    public void RecordGif()
    {
        if (!_hasSelection || _selection.Width < 1 || _selection.Height < 1)
        {
            return;
        }

        CommitText();

        GifRegion = SelectionScreenRect();
        Result = null;
        CopiedOnly = false;
        DialogResult = true;
    }

    /// <summary>
    /// 右下角「全屏」：不框选，直接按整屏录。选区状态全部丢掉，
    /// 遮罩先关掉（不然会被录进去），录制由 <see cref="Services.GifRecordingService.StartFullScreen"/>
    /// 接手 —— 多屏选屏、倒计时开关都在那条现成的路径里。
    /// </summary>
    public void RecordGifFullScreen()
    {
        if (!_gifMode)
        {
            return;
        }

        CommitText();

        _dragging = false;
        _hasSelection = false;
        _selection = Rect.Empty;
        _hadSelectionBeforeReframe = false;
        _selectionBeforeReframe = Rect.Empty;
        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        ClearAnnotations();
        ShowFullDim();

        GifRegion = null;
        GifFullScreenRequested = true;
        Result = null;
        CopiedOnly = false;
        DialogResult = true;
    }

    /// <summary>
    /// 选区换算成屏幕**物理像素**的矩形（GDI 抓屏要的就是这个坐标系）。
    /// 遮罩是铺在整个虚拟屏上的，所以「选区 × 截图/窗口 的像素比」再加虚拟屏原点即可。
    /// </summary>
    private System.Drawing.Rectangle SelectionScreenRect()
    {
        var bounds = Services.ScreenCaptureService.GetVirtualScreenBounds();
        var sx = ScaleX;
        var sy = ScaleY;

        var x = bounds.Left + (int)Math.Round(_selection.X * sx);
        var y = bounds.Top + (int)Math.Round(_selection.Y * sy);
        var width = Math.Max(4, (int)Math.Round(_selection.Width * sx));
        var height = Math.Max(4, (int)Math.Round(_selection.Height * sy));

        // 越界坐标不要送给 CopyFromScreen（多屏拼接时容易算出界）
        var right = Math.Min(bounds.Right, x + width);
        var bottom = Math.Min(bounds.Bottom, y + height);
        x = Math.Clamp(x, bounds.Left, bounds.Right);
        y = Math.Clamp(y, bounds.Top, bounds.Bottom);

        return new System.Drawing.Rectangle(
            x, y,
            Math.Max(4, right - x),
            Math.Max(4, bottom - y));
    }

    /// <summary>
    /// 把图片放进剪贴板：大图要几百毫秒，放独立 STA 线程后台做，
    /// 覆盖层立刻关闭，不让 UI 线程跟着卡。图已 Freeze，跨线程安全。
    /// </summary>
    private static void CopyToClipboardInBackground(BitmapSource image)
    {
        var thread = new System.Threading.Thread(() =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetImage(image);
                    return;
                }
                catch (Exception)
                {
                    // 剪贴板同一时间只能被一个程序占用，被别的程序短暂占着时重试几次
                    if (attempt >= 3)
                    {
                        return;
                    }

                    System.Threading.Thread.Sleep(120);
                }
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// 同步写剪贴板（回归测试用，保证断言时剪贴板已经写完）。
    /// 被别的程序短暂占着时重试几次，别让用户以为没复制上。
    /// </summary>
    private static bool CopyToClipboard(BitmapSource image)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return true;
            }
            catch (Exception)
            {
                System.Threading.Thread.Sleep(60);
            }
        }

        return false;
    }

    /// <summary>给回归用：把指定图片放进剪贴板（走和「复制」按钮同一条路径）。</summary>
    internal static bool CopyForTest(BitmapSource image) => CopyToClipboard(image);

    /// <summary>给回归用：不走对话框，直接拿到合成结果。</summary>
    internal BitmapSource? ComposeForTest() => Compose();

    /// <summary>把当前选区与标注合成为一张图（像素尺寸 = 截图原生尺寸）。</summary>
    private BitmapSource? Compose()
    {
        if (!_hasSelection || _selection.Width < 1 || _selection.Height < 1)
        {
            return null;
        }

        CommitText();

        var sx = ScaleX;
        var sy = ScaleY;

        var pxX = (int)Math.Round(_selection.X * sx);
        var pxY = (int)Math.Round(_selection.Y * sy);
        var pxW = Math.Max(1, (int)Math.Round(_selection.Width * sx));
        var pxH = Math.Max(1, (int)Math.Round(_selection.Height * sy));

        var crop = Services.ScreenCaptureService.Crop(_screenshot, new Int32Rect(pxX, pxY, pxW, pxH));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(crop, new Rect(0, 0, pxW, pxH));

            if (_strokes.Count > 0 || _editingText is not null)
            {
                // 标注是在 DIP 坐标系里画的；用放大后的 DPI 渲染，正好落到截图的原生像素上
                var anno = new RenderTargetBitmap(pxW, pxH, 96 * sx, 96 * sy, PixelFormats.Pbgra32);
                anno.Render(AnnoLayer);
                dc.DrawImage(anno, new Rect(0, 0, pxW, pxH));
            }
        }

        var result = new RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();

        return result;
    }

    /// <summary>选区的屏幕坐标（DIP），贴图窗口就摆在这个位置。</summary>
    public Point SelectionTopLeft => new(Left + _selection.X, Top + _selection.Y);

    /// <summary>给回归用：覆盖层根元素（读实际尺寸）。</summary>
    internal FrameworkElement RootForTest => Root;

    /// <summary>给回归用：背景图元素。</summary>
    internal System.Windows.Controls.Image BackdropForTest => Backdrop;

    /// <summary>给回归用：把覆盖层固定成指定尺寸（用合成图测试时不必铺满桌面）。</summary>
    internal void UseExactSizeForTest(double width, double height)
    {
        _exactSizeForTest = true;
        Left = 0;
        Top = 0;
        Width = width;
        Height = height;
    }

    /// <summary>给回归用：按像素坐标选一块区域。</summary>
    internal void SelectForTest(Rect dipRect)
    {
        _selection = Clamp(dipRect);
        _hasSelection = true;
        Hint.Visibility = Visibility.Collapsed;
        ApplySelectionVisual(_selection, showHandles: true);
        ShowToolbar();
    }

    /// <summary>给回归用：直接用某个工具画一笔（坐标为选区内的 DIP）。</summary>
    internal void DrawForTest(string tool, Point from, Point to)
    {
        _tool = tool switch
        {
            "pen" => Tool.Pen,
            "line" => Tool.Line,
            "rect" => Tool.Rect,
            "text" => Tool.Text,
            _ => Tool.Select,
        };

        if (_tool == Tool.Pen)
        {
            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(_color),
                StrokeThickness = _thickness,
                StrokeLineJoin = PenLineJoin.Round,
            };

            for (var t = 0.0; t <= 1.0; t += 0.1)
            {
                polyline.Points.Add(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t));
            }

            AnnoLayer.Children.Add(polyline);
            _strokes.Add(polyline);
        }
        else if (_tool == Tool.Line)
        {
            var line = new Line
            {
                X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y,
                Stroke = new SolidColorBrush(_color),
                StrokeThickness = _thickness,
            };
            AnnoLayer.Children.Add(line);
            _strokes.Add(line);
        }
        else if (_tool == Tool.Rect)
        {
            var rect = new Rectangle
            {
                Stroke = new SolidColorBrush(_color),
                StrokeThickness = _thickness,
                Width = Math.Abs(to.X - from.X),
                Height = Math.Abs(to.Y - from.Y),
            };
            Canvas.SetLeft(rect, Math.Min(from.X, to.X));
            Canvas.SetTop(rect, Math.Min(from.Y, to.Y));
            AnnoLayer.Children.Add(rect);
            _strokes.Add(rect);
        }
    }

    /// <summary>给回归用：在选区里加一段文字。</summary>
    internal void AddTextForTest(Point at, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(_color),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        };

        Canvas.SetLeft(block, at.X);
        Canvas.SetTop(block, at.Y);
        AnnoLayer.Children.Add(block);
        _strokes.Add(block);
    }

    /// <summary>给回归用：改画笔粗细 / 颜色。</summary>
    internal void SetBrushForTest(double thickness, Color color)
    {
        _thickness = thickness;
        _color = color;
    }
}
