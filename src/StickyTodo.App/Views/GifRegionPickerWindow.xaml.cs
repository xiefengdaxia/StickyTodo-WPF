using System.Windows;
using System.Windows.Input;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Views;

/// <summary>
/// 录屏专用框选层：只做「拖一块区域」或「整块屏幕」两件事，
/// 刻意不带截图那套画笔 / 文字 / 贴图工具，免得两件事混在一起。
/// 边框用红色，和截图的蓝色调区分开。
/// </summary>
public partial class GifRegionPickerWindow : Window
{
    private Point _origin;
    private bool _dragging;
    private bool _hasSelection;
    private Rect _selection;

    private readonly System.Drawing.Rectangle? _suggestedRegionPx;

    public GifRegionPickerWindow(System.Drawing.Rectangle? suggestedRegion = null)
    {
        InitializeComponent();

        _suggestedRegionPx = suggestedRegion;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        TopHint.Text = Loc.T("gif.pickerHint");
        ScopeHint.Text = Loc.T("gif.pickerScope");
        FullScreenButton.Content = Loc.T("gif.fullscreen");
        CancelButton.Content = Loc.T("common.cancel");
        StartButton.Content = Loc.T("gif.start");
        StartButton.IsEnabled = false;

        Loaded += (_, _) =>
        {
            // 窗口这会儿才接上 PresentationSource，PointFromScreen 才能用
            if (_suggestedRegionPx is { } suggested && suggested.Width > 0 && suggested.Height > 0)
            {
                var topLeft = PointFromScreen(new Point(suggested.Left, suggested.Top));
                var bottomRight = PointFromScreen(new Point(suggested.Right, suggested.Bottom));

                if (topLeft.X >= 0 && topLeft.Y >= 0)
                {
                    SetSelection(new Rect(topLeft, bottomRight));
                    StartButton.IsEnabled = true;
                }
            }

            Activate();
            Focus();
            PlaceToolbar();
        };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>用户选中的区域（屏幕物理像素）；选了「全屏」时为 null。</summary>
    public System.Drawing.Rectangle? SelectedRegion { get; private set; }

    /// <summary>用户点了「全屏」。调用方按整块屏/整块虚拟屏处理。</summary>
    public bool FullScreenChosen { get; private set; }

    // ---------------- 鼠标 ----------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount >= 2 && _hasSelection)
        {
            ConfirmSelection();
            return;
        }

        _origin = e.GetPosition(Root);
        _dragging = true;
        _hasSelection = false;
        StartButton.IsEnabled = false;
        Selection.Visibility = Visibility.Collapsed;
        SizeChip.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging)
        {
            return;
        }

        var current = e.GetPosition(Root);
        var rect = new Rect(
            Math.Min(_origin.X, current.X),
            Math.Min(_origin.Y, current.Y),
            Math.Abs(current.X - _origin.X),
            Math.Abs(current.Y - _origin.Y));

        SetSelection(rect);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        if (_hasSelection)
        {
            StartButton.IsEnabled = true;
            Toolbar.Visibility = Visibility.Visible;
            PlaceToolbar();
        }
        else
        {
            Toolbar.Visibility = Visibility.Visible;
        }
    }

    // ---------------- 选区绘制 ----------------

    private void SetSelection(Rect rect)
    {
        if (rect.Width < 4 || rect.Height < 4)
        {
            _hasSelection = false;
            Selection.Visibility = Visibility.Collapsed;
            SizeChip.Visibility = Visibility.Collapsed;
            return;
        }

        _selection = rect;
        _hasSelection = true;

        Selection.Width = rect.Width;
        Selection.Height = rect.Height;
        System.Windows.Controls.Canvas.SetLeft(Selection, rect.X);
        System.Windows.Controls.Canvas.SetTop(Selection, rect.Y);
        Selection.Visibility = Visibility.Visible;

        var px = ToPixelRect(rect);
        SizeText.Text = $"{px.Width} × {px.Height}";
        SizeChip.Visibility = Visibility.Visible;

        var chipY = rect.Y >= 34 ? rect.Y - 30 : rect.Y + 6;
        System.Windows.Controls.Canvas.SetLeft(SizeChip, rect.X);
        System.Windows.Controls.Canvas.SetTop(SizeChip, chipY);

        Toolbar.Visibility = Visibility.Visible;
        PlaceToolbar();
    }

    private void PlaceToolbar()
    {
        Toolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Toolbar.DesiredSize.Width;
        var height = Toolbar.DesiredSize.Height;

        var anchor = _hasSelection && _selection.Height > height + 24
            ? _selection
            : new Rect(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

        var left = anchor.X + ((anchor.Width - width) / 2);
        var top = anchor.Y + anchor.Height - height - 16;

        if (anchor.Height <= height + 24)
        {
            top = anchor.Y + anchor.Height - height - 16;
        }

        TopHint.Margin = new Thickness(0);
        System.Windows.Controls.Canvas.SetLeft(Toolbar, Math.Max(12, Math.Min(left, Width - width - 12)));
        System.Windows.Controls.Canvas.SetTop(Toolbar, Math.Max(12, Math.Min(top, Height - height - 12)));

        System.Windows.Controls.Canvas.SetLeft(TopHint, Math.Max(12, (Width - 520) / 2));
        System.Windows.Controls.Canvas.SetTop(TopHint, 24);
    }

    private Rect ToDipRect(System.Drawing.Rectangle px)
    {
        var topLeft = PointFromScreen(new Point(px.Left, px.Top));
        var bottomRight = PointFromScreen(new Point(px.Right, px.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private System.Drawing.Rectangle ToPixelRect(Rect dip)
    {
        var topLeft = PointToScreen(new Point(dip.Left, dip.Top));
        var bottomRight = PointToScreen(new Point(dip.Right, dip.Bottom));

        var screen = Services.ScreenCaptureService.GetVirtualScreenBounds();
        var left = Math.Max(screen.Left, (int)Math.Round(topLeft.X));
        var top = Math.Max(screen.Top, (int)Math.Round(topLeft.Y));
        var right = Math.Min(screen.Right, (int)Math.Round(bottomRight.X));
        var bottom = Math.Min(screen.Bottom, (int)Math.Round(bottomRight.Y));

        return new System.Drawing.Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // ---------------- 确认 ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWith(false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_hasSelection)
            {
                ConfirmSelection();
            }
            else
            {
                ConfirmFullScreen();
            }

            e.Handled = true;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void ConfirmSelection()
    {
        var region = ToPixelRect(_selection);
        if (region.Width < 4 || region.Height < 4)
        {
            return;
        }

        SelectedRegion = region;
        FullScreenChosen = false;
        CloseWith(true);
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ConfirmFullScreen();

    private void ConfirmFullScreen()
    {
        SelectedRegion = null;
        FullScreenChosen = true;
        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);

    /// <summary>ShowDialog 之外被调用时别抛异常，直接关掉。</summary>
    private void CloseWith(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }
}
