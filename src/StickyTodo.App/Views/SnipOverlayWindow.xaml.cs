using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace StickyTodo.App.Views;

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

    public SnipOverlayWindow(BitmapSource screenshot)
    {
        InitializeComponent();

        _screenshot = screenshot;
        Backdrop.Source = screenshot;

        // 初始：整屏压暗，没有"亮洞"（尺寸在 Loaded 时按窗口大小再设一次）
        ShowFullDim();
    }

    /// <summary>整屏压暗（无选区）。</summary>
    private void ShowFullDim()
    {
        DimFull.Visibility = Visibility.Visible;
        DimFull.Width = ActualWidth > 0 ? ActualWidth : Width;
        DimFull.Height = ActualHeight > 0 ? ActualHeight : Height;

        foreach (var rect in new[] { DimTop, DimBottom, DimLeft, DimRight })
        {
            rect.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>把压暗层切成四块，围出一个亮的选区。</summary>
    private void ShowDimAround(Rect selection)
    {
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;

        DimFull.Visibility = Visibility.Collapsed;

        Position(DimTop, 0, 0, w, selection.Y);
        Position(DimBottom, 0, selection.Bottom, w, Math.Max(0, h - selection.Bottom));
        Position(DimLeft, 0, selection.Y, selection.X, selection.Height);
        Position(DimRight, selection.Right, selection.Y, Math.Max(0, w - selection.Right), selection.Height);

        static void Position(FrameworkElement element, double left, double top, double width, double height)
        {
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            element.Width = Math.Max(0, width);
            element.Height = Math.Max(0, height);
            element.Visibility = height > 0 && width > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 铺满整个虚拟桌面（多显示器也是一整块）
        if (!_exactSizeForTest)
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };

        PreviewKeyDown += OnPreviewKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
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
                SelectionBorder.Visibility = Visibility.Collapsed;
                ShowFullDim();
                Hint.Visibility = Visibility.Visible;
                return;
            }

            _hasSelection = true;
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

        ShowDimAround(rect);

        Canvas.SetLeft(SelectionBorder, rect.X);
        Canvas.SetTop(SelectionBorder, rect.Y);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        SelectionBorder.Visibility = Visibility.Visible;

        Canvas.SetLeft(AnnoLayer, rect.X);
        Canvas.SetTop(AnnoLayer, rect.Y);
        AnnoLayer.Width = rect.Width;
        AnnoLayer.Height = rect.Height;

        SizeText.Text = $"{(int)Math.Round(rect.Width * ScaleX)} × {(int)Math.Round(rect.Height * ScaleY)}";
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
            if (ReferenceEquals(node, Toolbar))
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

    private void Pin_Click(object sender, RoutedEventArgs e) => Pin();

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
        DialogResult = true;
    }

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
